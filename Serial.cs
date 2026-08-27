namespace rt4k_pi;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

// Host side of the RetroTINK-4K serial control protocol (see PROTOCOL.md).
//
// The device exposes two planes on the same wire:
//  - The text plane (default): CR/LF terminated ASCII commands, replies prefixed "[COM] ".
//  - The binary plane (RTL1): opened by a transfer command (get/put/sget/osd/font/rtl1 echo),
//    length delimited CRC checked frames, closed again when the session ends.
//
// Only one binary session may be open at a time on the device (globally, across both of its
// ports), so all sessions here are serialized behind a single lock. The device also only
// buffers one pending text command, so commands issued through SendCommand are serialized too.

public class SerialException(string message) : Exception(message);

public class Serial
{
    public const int MaxPayload = Rtl1.MaxPayload;

    // Inactivity timeout the firmware applies to get/sget/osd/put sessions (XFER_IDLE_MS)
    private const int XferIdleMs = 4000;

    // The firmware's rtl1 echo session self-closes after ECHO_IDLE_MS
    private const int EchoIdleMs = 3000;

    // Grace period after a command's terminal line for trailing replies to arrive, so the echo
    // capture covers the whole exchange rather than half of it (see SendCommandCoreAsync).
    private const int TrailingReplyMs = 50;

    public bool IsConnected { get; private set; }

    private Stream port = null!;
    private readonly HashSet<Action<byte[]>> readers = [];
    private readonly HashSet<Action<string>> stringReaders = [];
    private readonly Encoding encoding = Encoding.Latin1;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly SemaphoreSlim commandLock = new(1, 1);
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private readonly StringBuilder lineBuffer = new();
    private readonly Rtl1Decoder decoder;

    // Decouples the raw drain from all downstream processing (see HandleRead)
    private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // While non-null, console echo is captured instead of printed so the caller can decide
    // afterwards whether the exchange was worth showing (see SendCommandAsync's echoIf).
    private readonly Lock echoLock = new();
    private List<(string Text, ConsoleColor Color)>? echoCapture;

    // While set, device text is discarded rather than logged. Used by timer-driven sessions
    // whose chatter would otherwise bury everything else in the debug log.
    private bool echoMuted;

    private event Action<string>? LineReceived;

    /// <summary>
    /// Every line the device sends, including replies to commands issued elsewhere. Lets
    /// listeners react to device-initiated activity without owning the command plane.
    /// </summary>
    public event Action<string>? LineObserved;

    // Non-null only while a binary (RTL1) session is open
    private Channel<Rtl1Frame>? frames;
    private Channel<string>? sessionLines;

    // True only while framed bytes are actually on the wire. The session-opening command is a
    // normal text line and should still be echoed, so this is set after the ready line arrives
    // rather than keyed off "frames".
    private bool binaryPhase;

    private static readonly bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public Serial(int baudRate)
    {
        decoder = new Rtl1Decoder(HandleFrame, HandleText);

        Task.Run(async () =>
        {
            if (GetPort() == null)
            {
                Console.WriteLine("Serial port does not exist, waiting for connection.");
            }

            // The drain must never be descheduled while bytes are arriving with no flow control,
            // so it gets a dedicated high priority thread rather than a pool thread.
            var readThread = new Thread(HandleRead) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Serial drain" };
            readThread.Start();

            _ = Task.Run(ProcessRead, cts.Token);

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (IsConnected && !isWindows && !File.Exists((port as FileStream)?.Name ?? ""))
                    {
                        throw new IOException("Serial port disconnected");
                    }
                    else if (!IsConnected)
                    {
                        var currentPort = GetPort();
                        if (currentPort != null)
                        {
                            Console.WriteLine($"Detected serial port at {currentPort}");
                            Console.WriteLine($"Connecting to {currentPort}");

                            if (!isWindows)
                            {
                                // crtscts is mandatory: streaming put relies on the device raising
                                // CTS# when its 2048 byte RX ring hits HIWATER. Without it we blast
                                // 2 Mbaud at a ring that cannot push back and silently overrun it.
                                Util.RunCommand("stty", $"-F {currentPort} {baudRate} cs8 -cstopb -parenb crtscts");
                                SetFtdiLatencyTimer(currentPort);
                                port = new FileStream(currentPort, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            }
                            else
                            {
#if USE_SYSTEM_IO_PORTS
                                port = WindowsSerialPort.OpenAndConfigure(currentPort, baudRate);
#else
                                throw new PlatformNotSupportedException("System.IO.Ports is excluded from non-Windows builds.");
#endif
                            }
                            Console.WriteLine($"Connected to {currentPort}");
                            IsConnected = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Serial error: {ex.Message}");
                    IsConnected = false;
                    port?.Dispose();
                }

                await Task.Delay(2000);
            }
        }, cts.Token);
    }

    ~Serial()
    {
        cts.Cancel();
        port?.Close();
    }

    // The FTDI driver defaults to a 16 ms latency timer: it won't forward a short read up to the
    // host until 62 bytes accumulate or the timer expires. That penalises every small exchange
    // (command replies, the terminal "done" line, and the trailing partial frame of a transfer).
    // Dropping it to 1 ms removes that dead air.
    private static void SetFtdiLatencyTimer(string portName, int milliseconds = 1)
    {
        try
        {
            string device = Path.GetFileName(portName);

            foreach (string path in Directory.GetFiles($"/sys/bus/usb-serial/devices/{device}", "latency_timer"))
            {
                File.WriteAllText(path, milliseconds.ToString());
                Console.WriteLine($"Set FTDI latency timer to {milliseconds} ms on {device}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not set the FTDI latency timer ({ex.Message}). Transfers will be slow.");
        }
    }

    private static string? GetPort()
    {
        if (isWindows)
        {
#if USE_SYSTEM_IO_PORTS
            return WindowsSerialPort.FindPort();
#else
            return null;
#endif
        }
        else
        {
            return Directory.GetFiles("/dev", "ttyUSB*").FirstOrDefault();
        }
    }

    #region Reading

    // Streaming downloads are not flow-controlled in any way: the device transmits flat out and
    // nothing can slow it down. So the only job of this thread is to drain the OS buffer as fast
    // as physically possible. All decoding, echoing and fan-out happens on ProcessRead so that a
    // slow console write or a slow registered reader can never stall the drain and lose bytes.
    private void HandleRead()
    {
        Console.WriteLine("Starting serial read loop");

        while (!cts.Token.IsCancellationRequested)
        {
            if (!IsConnected)
            {
                Thread.Sleep(100);
                continue;
            }

            byte[] readBuf = new byte[65536];
            int read = 0;

            try
            {
                // Blocks until there's data
                read = port.Read(readBuf, 0, readBuf.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Serial error: {ex.Message}");
                IsConnected = false;
                port.Dispose();
                continue;
            }

            if (read <= 0)
            {
                continue;
            }

            incoming.Writer.TryWrite(readBuf[0..read]);
        }
    }

    private async Task ProcessRead()
    {
        await foreach (byte[] data in incoming.Reader.ReadAllAsync(cts.Token))
        {
            // Raw readers (ser2net and friends) always see the unmodified stream
            foreach (Action<byte[]> action in readers.ToArray())
            {
                try
                {
                    action(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: error calling registered reader: {ex.Message}");
                }
            }

            if (frames != null)
            {
                // A binary session is open: pull out RTL1 frames, anything else is still text
                // (the device prints its terminal "[COM] <verb> done" line on the text plane).
                decoder.Feed(data);
            }
            else
            {
                HandleText(data);
            }
        }
    }

    private void HandleFrame(Rtl1Frame frame) => frames?.Writer.TryWrite(frame);

    private void HandleText(byte[] data)
    {
        string receivedData = encoding.GetString(data);

        Echo(receivedData, ConsoleColor.Green);

        lock (lineBuffer)
        {
            lineBuffer.Append(receivedData);

            // Some safety precautions
            if (lineBuffer.Length > 32768)
            {
                Console.WriteLine("Warning: Serial read buffer exceeded 32K, purging read buffer");
                lineBuffer.Clear();
            }
        }

        while (true)
        {
            string line;

            lock (lineBuffer)
            {
                int newlineIndex = -1;

                for (int i = 0; i < lineBuffer.Length; i++)
                {
                    if (lineBuffer[i] == '\n')
                    {
                        newlineIndex = i;
                        break;
                    }
                }

                if (newlineIndex == -1)
                {
                    return;
                }

                line = lineBuffer.ToString(0, newlineIndex).TrimEnd('\r');
                lineBuffer.Remove(0, newlineIndex + 1);
            }

            DispatchLine(line);
        }
    }

    private void DispatchLine(string line)
    {
        foreach (Action<string> stringAction in stringReaders.ToArray())
        {
            try
            {
                stringAction(line);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: error calling registered string reader: {ex.Message}");
            }
        }

        try
        {
            LineReceived?.Invoke(line);
            LineObserved?.Invoke(line);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: error dispatching serial line: {ex.Message}");
        }

        sessionLines?.Writer.TryWrite(line);
    }

    public void RegisterReader(Action<byte[]> reader) => readers.Add(reader);
    public void RegisterReader(Action<string> reader) => stringReaders.Add(reader);

    public void UnregisterReader(Action<byte[]> reader) => readers.Remove(reader);
    public void UnregisterReader(Action<string> reader) => stringReaders.Remove(reader);

    #endregion

    #region Writing

    public void WriteLine(string data) => Write(encoding.GetBytes(data + '\n'));

    public void Write(byte[] data)
    {
        if (!IsConnected)
        {
            return;
        }

        // Don't echo binary frames to the console
        if (!binaryPhase)
        {
            Echo(encoding.GetString(data), ConsoleColor.DarkRed);
        }

        writeLock.Wait();

        try
        {
            port.Write(data);
            port.Flush();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Serial error: {ex.Message}");
            IsConnected = false;
            port.Dispose();
        }
        finally
        {
            writeLock.Release();
        }
    }

    private void WriteFrame(ushort nonce, Rtl1Type type, byte seq, ReadOnlySpan<byte> payload = default)
    {
        LogFrameVerbose("TX", type, seq, payload.Length);
        Write(Rtl1.Encode(nonce, type, seq, payload));
    }

    // Per-frame chatter is far too noisy for the normal log, so it's verbose only
    private static void LogFrameVerbose(string direction, Rtl1Type type, byte seq, int length)
    {
        if (Program.Settings.VerboseLogging)
        {
            Console.WriteLine($"[RTL1] {direction} {type} seq={seq} len={length}");
        }
    }

    private void Echo(string text, ConsoleColor color)
    {
        lock (echoLock)
        {
            // A quiet session drops its text outright: unlike echoCapture there is nothing to
            // replay later, and the session's trailing "[COM] <verb> done" arrives long after
            // the opening command's capture has been flushed.
            if (echoMuted)
            {
                return;
            }

            if (echoCapture != null)
            {
                echoCapture.Add((text, color));
                return;
            }

            // Pass the colour explicitly rather than via Console.ForegroundColor: that is a
            // process-global that other writers reset out from under us, which randomly stripped
            // the colour off sent commands.
            Logger.Write(text, color);
        }
    }

    private void FlushEcho(bool print)
    {
        List<(string Text, ConsoleColor Color)> captured;

        lock (echoLock)
        {
            captured = echoCapture ?? [];
            echoCapture = null;
        }

        if (!print)
        {
            return;
        }

        foreach (var (text, color) in captured)
        {
            Echo(text, color);
        }
    }

    #endregion

    #region Text command plane

    // Strips the device's "[COM] " marker, returns null for anything that isn't a command reply
    public static string? StripComPrefix(string line) => line.StartsWith("[COM] ") ? line[6..] : null;

    /// <summary>
    /// Sends a command line and collects the "[COM] " reply lines (with the marker stripped).
    /// Collection stops when <paramref name="isTerminal"/> matches a line, or on timeout.
    /// When <paramref name="echoIf"/> is supplied, the console echo of the command and its reply
    /// is held back until it has run against the collected lines, so noisy polling commands can
    /// keep quiet unless something actually changed.
    /// </summary>
    public async Task<List<string>> SendCommandAsync(string command, Func<string, bool>? isTerminal = null, int timeoutMs = 1000, CancellationToken token = default, Func<List<string>, bool>? echoIf = null)
    {
        // A binary session owns the wire for its duration: an ASCII command sent into an open
        // RTL1 stream desyncs it and the transfer dies on a timeout, so wait our turn.
        await sessionLock.WaitAsync(token);

        try
        {
            return await SendCommandCoreAsync(command, isTerminal, timeoutMs, token, echoIf);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    // The command plane itself, without the session gate. Callers that already hold sessionLock
    // (i.e. the session openers in RunSessionAsync) must use this to avoid deadlocking on it.
    private async Task<List<string>> SendCommandCoreAsync(string command, Func<string, bool>? isTerminal, int timeoutMs, CancellationToken token, Func<List<string>, bool>? echoIf)
    {
        if (!IsConnected)
        {
            throw new SerialException($"Not connected to the {RT4K.DisplayName}");
        }

        await commandLock.WaitAsync(token);

        try
        {
            var lines = new ConcurrentQueue<string>();
            var complete = new TaskCompletionSource();

            void handler(string line)
            {
                string? reply = StripComPrefix(line);

                if (reply == null)
                {
                    return;
                }

                lines.Enqueue(reply);

                if (isTerminal != null && isTerminal(reply))
                {
                    complete.TrySetResult();
                }
            }

            LineReceived += handler;

            if (echoIf != null)
            {
                lock (echoLock)
                {
                    echoCapture = [];
                }
            }

            try
            {
                WriteLine(command);

                if (isTerminal == null)
                {
                    await Task.Delay(timeoutMs, token);
                }
                else
                {
                    // A timeout is a normal outcome (the device is off, or the command is a no-op)
                    await Task.WhenAny(complete.Task, Task.Delay(timeoutMs, token));

                    // The terminal line is not always the last thing the device sends: "status"
                    // ends on "status oerr=" but still emits "status profile=..." afterwards.
                    // Tearing the echo capture down immediately would let those trailing bytes
                    // print live and then get replayed by FlushEcho, duplicating and splitting
                    // them mid-line. Give the tail a moment to arrive first.
                    if (complete.Task.IsCompleted && echoIf != null)
                    {
                        await Task.Delay(TrailingReplyMs, token);
                    }
                }
            }
            finally
            {
                LineReceived -= handler;

                if (echoIf != null)
                {
                    FlushEcho(echoIf([.. lines]));
                }
            }

            return [.. lines];
        }
        finally
        {
            commandLock.Release();
        }
    }

    public List<string> SendCommand(string command, Func<string, bool>? isTerminal = null, int timeoutMs = 1000)
        => SendCommandAsync(command, isTerminal, timeoutMs).GetAwaiter().GetResult();

    /// <summary>
    /// Splits a device reply like "status fw=1.72.0 tag=f0807m" into its key=value pairs.
    /// </summary>
    public static Dictionary<string, string> ParseFields(string line)
    {
        var fields = new Dictionary<string, string>();

        foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = token.IndexOf('=');

            if (equals > 0)
            {
                fields[token[..equals]] = token[(equals + 1)..];
            }
        }

        return fields;
    }

    private static ushort ParseNonce(string readyLine)
    {
        if (!ParseFields(readyLine).TryGetValue("nonce", out string? nonce))
        {
            throw new SerialException($"No session nonce in ready line: {readyLine}");
        }

        return nonce.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? (ushort)Convert.ToUInt32(nonce[2..], 16)
            : ushort.Parse(nonce);
    }

    #endregion

    #region RTL1 binary sessions

    /// <summary>Downloads a file (or a range of one) from the RT4K's SD card.</summary>
    /// <remarks>
    /// TODO: this buffers the entire response in RAM (twice, in fact: the MemoryStream inside
    /// ReceiveAsync plus the byte[] it returns). The Pi Zero 2 W only has 512 MB total and we
    /// don't get all of it, so a large enough file will OOM or push us into swap. The protocol
    /// already supports ranged reads via -o/-l, so the fix is to stream to a caller-supplied
    /// Stream (or expose a chunked/ranged API) instead of materialising a byte[]. Needed before
    /// the FUSE/SMB layer can serve arbitrary files rather than small config blobs.
    /// </remarks>
    public async Task<byte[]> GetFileAsync(string path, long offset = 0, long length = 0, CancellationToken token = default, bool quiet = false)
    {
        string command = "get" + (offset > 0 ? $" -o {offset}" : "") + (length > 0 ? $" -l {length}" : "") + $" {path}";

        return await RunSessionAsync(command, "get", "get done", async ready =>
        {
            var fields = ParseFields(ready);
            byte[] data = await ReceiveAsync(ParseNonce(ready), token);

            if (fields.TryGetValue("len", out string? len) && long.TryParse(len, out long expected) && data.Length != expected)
            {
                throw new SerialException($"get returned {data.Length} bytes, expected {expected}");
            }

            return data;
        }, token, quiet: quiet);
    }

    /// <summary>Reads the entire live RT4K_state struct out of the device's RAM.</summary>
    public async Task<byte[]> GetStateAsync(CancellationToken token = default)
        => await RunSessionAsync("sget", "sget", "get done", async ready => await ReceiveAsync(ParseNonce(ready), token), token);

    /// <summary>Downloads the custom OSD glyph ROM (4096 bytes).</summary>
    /// <remarks>The device closes this session with "get done", not "font done".</remarks>
    public async Task<byte[]> GetFontAsync(CancellationToken token = default, bool quiet = false)
        => await RunSessionAsync("font", "font", "get done", async ready => await ReceiveAsync(ParseNonce(ready), token), token, quiet: quiet);

    /// <summary>
    /// Mirrors the on-screen OSD grid. Returns the text and color planes plus the ready line's
    /// fields (rows/stride/width/cells for the main plane, rows/cols/on/osk for the aux plane).
    /// </summary>
    public async Task<(byte[] Text, byte[] Color, Dictionary<string, string> Info)> GetOsdAsync(bool aux = false, CancellationToken token = default, bool quiet = false)
    {
        string verb = aux ? "osd2" : "osd";

        return await RunSessionAsync(verb, verb, "osd done", async ready =>
        {
            var info = ParseFields(ready);
            byte[] data = await ReceiveAsync(ParseNonce(ready), token);

            if (data.Length % 2 != 0)
            {
                throw new SerialException($"{verb} returned an odd number of bytes ({data.Length})");
            }

            int half = data.Length / 2;
            return (data[..half], data[half..], info);
        }, token, quiet: quiet);
    }

    /// <summary>Round-trips a payload through the device's RTL1 loopback session (transport proof).</summary>
    public async Task<byte[]> EchoAsync(byte[] payload, CancellationToken token = default)
        => (await EchoManyAsync(payload, 1, token)).Last;

    /// <summary>
    /// Round-trips a payload <paramref name="rounds"/> times inside a single echo session, so the
    /// measurement reflects streaming throughput rather than per-session setup cost. Returns the
    /// last reply plus the time spent purely on the round trips.
    /// </summary>
    public async Task<(byte[] Last, TimeSpan Elapsed)> EchoManyAsync(byte[] payload, int rounds, CancellationToken token = default)
    {
        return await RunSessionAsync("rtl1 echo", "rtl1 echo", "rtl1 echo end", async ready =>
        {
            ushort nonce = ParseNonce(ready);
            byte[] last = [];
            byte seq = 0;

            // Only the round trips are timed: session open and the terminal line sit outside.
            var timer = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < rounds; i++)
            {
                WriteFrame(nonce, Rtl1Type.Data, seq, payload);
                Rtl1Frame reply = await ReadFrameAsync(nonce, EchoIdleMs, token);

                if (reply.Type != Rtl1Type.Resp)
                {
                    throw new SerialException($"rtl1 echo: unexpected {DescribeFrame(reply)}");
                }

                last = reply.Payload;
                seq++;
            }

            timer.Stop();

            WriteFrame(nonce, Rtl1Type.Abort, seq);
            return (last, timer.Elapsed);
        }, token);
    }

    /// <summary>
    /// Uploads a file to the RT4K's SD card. The device writes to a temp file and atomically
    /// renames it once the declared size and SHA-256 both check out.
    /// </summary>
    public async Task PutFileAsync(string path, byte[] data, CancellationToken token = default)
    {
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        string command = $"put {data.Length} {sha} {path}";

        await RunSessionAsync(command, "put", "put done", async ready =>
        {
            ushort nonce = ParseNonce(ready);
            byte seq = 0;

            // The streaming path pre-computes the wire byte count assuming canonical framing
            // (full 2048 byte payloads, one partial, then the empty EOF frame), so never
            // fragment differently or the device stalls until it times out. Backpressure is
            // entirely the device's CTS# line, which the port is configured to honour.
            for (int offset = 0; offset < data.Length; offset += MaxPayload)
            {
                int chunk = Math.Min(MaxPayload, data.Length - offset);
                WriteFrame(nonce, Rtl1Type.Data, seq, data.AsSpan(offset, chunk));
                seq++;
            }

            // EOF is an empty DATA frame
            WriteFrame(nonce, Rtl1Type.Data, seq);

            return await Task.FromResult(true);
        }, token, terminalTimeoutMs: 15000);
    }

    private async Task<T> RunSessionAsync<T>(string command, string verb, string terminalLine, Func<string, Task<T>> body, CancellationToken token, int terminalTimeoutMs = XferIdleMs, bool quiet = false)
    {
        if (!IsConnected)
        {
            throw new SerialException($"Not connected to the {RT4K.DisplayName}");
        }

        await sessionLock.WaitAsync(token);

        try
        {
            // Arm the binary plane before the command goes out so no frame can be missed
            frames = Channel.CreateUnbounded<Rtl1Frame>();
            sessionLines = Channel.CreateUnbounded<string>();
            decoder.Reset();

            // Callers that run on a timer pass quiet, so a once-a-second transfer doesn't bury
            // everything else in the log. Verbose still shows the lot. The mute has to cover the
            // whole session, not just the opening command, because the device's closing
            // "[COM] <verb> done" arrives after that command has already returned.
            bool print = !quiet || Program.Settings.VerboseLogging;

            lock (echoLock)
            {
                echoMuted = !print;
            }

            var replies = await SendCommandCoreAsync(command, line => line.StartsWith($"{verb} ready") || line.StartsWith($"{verb}:") || line.StartsWith($"{verb} err"), 2000, token, null);
            string ready = replies.LastOrDefault() ?? throw new SerialException($"{verb}: no response from the {RT4K.DisplayName}");

            if (!ready.StartsWith($"{verb} ready"))
            {
                throw new SerialException(ready);
            }

            if (print)
            {
                Console.WriteLine($"[RTL1] {verb}: binary session started");
            }

            binaryPhase = true;

            T result;

            try
            {
                result = await body(ready);
            }
            finally
            {
                binaryPhase = false;
            }

            if (print)
            {
                Console.WriteLine($"[RTL1] {verb}: binary session finished");
            }

            // The device prints its terminal line on the text plane once the session closes
            await WaitForSessionLineAsync(terminalLine, verb, terminalTimeoutMs, token);

            return result;
        }
        finally
        {
            binaryPhase = false;
            frames = null;
            sessionLines = null;

            lock (echoLock)
            {
                echoMuted = false;
            }

            sessionLock.Release();
        }
    }

    // Receives DATA frames until the terminal RESP frame, and verifies the SHA-256 it carries
    private async Task<byte[]> ReceiveAsync(ushort nonce, CancellationToken token)
    {
        using var received = new MemoryStream();
        byte expectedSeq = 0;

        while (true)
        {
            Rtl1Frame frame = await ReadFrameAsync(nonce, XferIdleMs, token);

            if (frame.Type == Rtl1Type.Data || frame.Type == Rtl1Type.Resp)
            {
                if (frame.Seq != expectedSeq)
                {
                    throw new SerialException($"RTL1 sequence gap: expected {expectedSeq}, got {frame.Seq}");
                }

                expectedSeq++;
            }

            switch (frame.Type)
            {
                case Rtl1Type.Data:
                    received.Write(frame.Payload);
                    break;

                case Rtl1Type.Resp:
                    byte[] data = received.ToArray();

                    if (!SHA256.HashData(data).AsSpan().SequenceEqual(frame.Payload))
                    {
                        throw new SerialException("RTL1 transfer failed the SHA-256 check");
                    }

                    return data;

                default:
                    throw new SerialException($"RTL1 transfer failed: {DescribeFrame(frame)}");
            }
        }
    }

    private async Task<Rtl1Frame> ReadFrameAsync(ushort nonce, int timeoutMs, CancellationToken token)
    {
        Channel<Rtl1Frame> channel = frames ?? throw new SerialException("No RTL1 session is open");

        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
            timeout.CancelAfter(timeoutMs);

            Rtl1Frame frame;

            try
            {
                frame = await channel.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                throw new SerialException("Timed out waiting for an RTL1 frame");
            }

            // Frames from a stale session carry a different nonce
            if (frame.Nonce == nonce)
            {
                LogFrameVerbose("RX", frame.Type, frame.Seq, frame.Payload.Length);
                return frame;
            }
        }
    }

    private async Task<string> WaitForSessionLineAsync(string terminalLine, string verb, int timeoutMs, CancellationToken token)
    {
        Channel<string> channel = sessionLines ?? throw new SerialException("No RTL1 session is open");

        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
            timeout.CancelAfter(timeoutMs);

            string line;

            try
            {
                line = await channel.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                throw new SerialException($"{verb}: timed out waiting for the session to close");
            }

            string? reply = StripComPrefix(line);

            if (reply == null)
            {
                continue;
            }

            if (reply.StartsWith(terminalLine))
            {
                return reply;
            }

            // Failure lines: "<verb> fail: ...", "<verb> err: ...", "<verb> timeout", "<verb> aborted"
            if (reply.StartsWith($"{verb} fail") || reply.StartsWith($"{verb} err") || reply.StartsWith($"{verb} timeout") || reply.StartsWith($"{verb} aborted"))
            {
                throw new SerialException(reply);
            }
        }
    }

    private static string DescribeFrame(Rtl1Frame frame)
        => frame.Type == Rtl1Type.Nak && frame.Payload.Length > 0
            ? $"NAK ({(Rtl1Nak)frame.Payload[0]})"
            : frame.Type.ToString().ToUpperInvariant();

    #endregion
}
