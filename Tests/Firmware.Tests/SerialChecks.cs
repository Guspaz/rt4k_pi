namespace FirmwareTests;

using System.Diagnostics;
using System.Reflection;
using System.Text;
using rt4k_pi;

internal static class SerialChecks
{
    private static (Serial Serial, Wire Wire) Create()
    {
        var serial = new Serial(2000000);
        var decoder = (Rtl1Decoder)typeof(Serial).GetField("decoder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        var wire = new Wire(decoder);
        typeof(Serial).GetField("port", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(serial, wire);
        return (serial, wire);
    }

    public static async Task TransferAsync()
    {
        var (serial, wire) = Create();
        await using var lifetime = serial;
        wire.HoldCompletion = true;
        byte[] bytes = Enumerable.Range(0, 257 * Serial.MaxPayload + 13).Select(i => (byte)(i % 251)).ToArray();
        var progress = new List<long>();
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = serial.RunExclusiveAsync(async token =>
        {
            using var stream = new MemoryStream(bytes);
            await serial.PutFirmwareFileAsync("test.rbf", stream, bytes.Length, Data.Hash(bytes), progress.Add, token);
            uploaded.SetResult();
            await release.Task;
        }, CancellationToken.None);
        try
        {
            await wire.EofReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (uploaded.Task.IsCompleted || progress[^1] != bytes.Length) { throw new Exception("Sent bytes were mistaken for device-verified completion."); }
            wire.Complete("put done");
            await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rt4k = new RT4K(serial);
            long revision = rt4k.Revision;
            await rt4k.RefreshPowerAsync();
            await rt4k.StopAsync();
            if (rt4k.Revision != revision) { throw new Exception("Maintenance poll changed cached status."); }
            try
            {
                await serial.SendTextCommandAsync("pwr off");
                throw new Exception("External command was admitted during firmware ownership.");
            }
            catch (SerialException) { }
        }
        finally { release.SetResult(); }
        await operation;
        if (serial.IsMaintenance || !wire.Received.ToArray().SequenceEqual(bytes) || wire.EofCount != 1 || wire.AbortCount != 0 || progress[^1] != bytes.Length || progress.Count != 258 || !wire.OpenCommand.StartsWith($"put {bytes.Length} {Data.Hash(bytes)} test.rbf"))
        {
            throw new Exception("Serial streaming, sent-byte progress, exclusive ownership, or EOF handling failed.");
        }
    }

    public static async Task VerificationFailureAsync()
    {
        var (serial, wire) = Create();
        await using var lifetime = serial;
        wire.HoldCompletion = true;
        byte[] bytes = new byte[Serial.MaxPayload];
        Task upload = serial.RunExclusiveAsync(async token =>
        {
            using var input = new MemoryStream(bytes);
            await serial.PutFirmwareFileAsync("test.rbf", input, bytes.Length, Data.Hash(bytes), _ => { }, token);
        }, CancellationToken.None);
        await wire.EofReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        wire.Complete("put fail: size/sha mismatch");
        try { await upload; throw new Exception("Device verification failure was accepted."); }
        catch (SerialException ex) when (ex.Message.Contains("size/sha mismatch")) { }
        if (serial.IsMaintenance) { throw new Exception("Failed upload retained the serial lease."); }
    }

    public static async Task RebootTextAsync()
    {
        var (serial, _) = Create();
        await using var lifetime = serial;
        var decoder = (Rtl1Decoder)typeof(Serial).GetField("decoder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        var lines = new List<string>();
        serial.RegisterReader((string line) => lines.Add(line));
        TextWriter output = Console.Out;
        using var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            var logger = new Logger();
            Console.SetOut(logger);
            decoder.Feed(new byte[4096]);
            decoder.Feed([0, 13]);
            decoder.Feed([10, 0, 0, 10]);
            if (logger.Snapshot().Length != 0 || lines.Count != 0) { throw new Exception("NUL-only reboot output leaked into the console or text readers."); }
            decoder.Feed(Encoding.Latin1.GetBytes("\0[COM] sta"));
            decoder.Feed(Encoding.Latin1.GetBytes("tus fw=1.77.0\0\r"));
            decoder.Feed(Encoding.Latin1.GetBytes("\n\r\nBoot ready\n"));
            const string expected = "[COM] status fw=1.77.0\r\n\r\nBoot ready\n";
            if (string.Concat(logger.Snapshot().Select(e => e.Entry)) != expected || !lines.SequenceEqual(new[] { "[COM] status fw=1.77.0", "", "Boot ready" }))
            {
                throw new Exception("Reboot filtering changed valid text, blank lines, or status parsing.");
            }
            if (captured.ToString().Contains('\0')) { throw new Exception("Console still contains NUL bytes."); }
        }
        finally { Console.SetOut(output); }
    }

    public static Task LoggingAsync()
    {
        TextWriter output = Console.Out;
        bool verbose = rt4k_pi.Program.Settings.VerboseLogging;
        using var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            var logger = new Logger();
            rt4k_pi.Program.Settings.VerboseLogging = false;
            logger.Write("info: request completed".ToCharArray(), 0, 23);
            logger.Write('\r');
            logger.Write('\n');
            if (logger.Snapshot().Length != 0 || captured.ToString().Length != 0) { throw new Exception("Suppressed request leaked blank lines."); }
            string visible = "Firmware transfer complete\n";
            logger.Write(visible.ToCharArray(), 0, visible.Length);
            if (string.Concat(logger.Snapshot().Select(e => e.Entry)) != visible) { throw new Exception("Normal logging changed."); }
            rt4k_pi.Program.Settings.VerboseLogging = true;
            string info = "info: visible request\n";
            logger.Write(info.ToCharArray(), 0, info.Length);
            if (!captured.ToString().Contains("info: visible request") || !captured.ToString().Contains("\x1B[")) { throw new Exception("Verbose logging or ANSI colors were lost."); }
        }
        finally
        {
            Console.SetOut(output);
            rt4k_pi.Program.Settings.VerboseLogging = verbose;
        }
        return Task.CompletedTask;
    }

    public static async Task CancelAsync()
    {
        var (serial, wire) = Create();
        await using var lifetime = serial;
        using var cancellation = new CancellationTokenSource();
        byte[] bytes = new byte[3 * Serial.MaxPayload];
        var watch = Stopwatch.StartNew();
        try
        {
            await serial.RunExclusiveAsync(async token =>
            {
                using var stream = new MemoryStream(bytes);
                await serial.PutFirmwareFileAsync("test.rbf", stream, bytes.Length, Data.Hash(bytes), _ => cancellation.Cancel(), token);
            }, cancellation.Token);
            throw new Exception("Upload ignored cancellation.");
        }
        catch (OperationCanceledException) { }
        if (wire.AbortCount != 1 || wire.EofCount != 0 || wire.Received.Length != Serial.MaxPayload || serial.IsMaintenance || watch.Elapsed < TimeSpan.FromSeconds(4.8))
        {
            throw new Exception("Canceled upload failed to abort and quarantine before releasing serial ownership.");
        }
        var reply = await serial.SendCommandAsync("status", line => line == "status oerr=0");
        if (!reply.Contains("status oerr=0")) { throw new Exception("Text plane did not recover after cancellation."); }
    }

    private sealed class Wire : Stream
    {
        private const ushort Nonce = 0x1234;
        private readonly Rtl1Decoder receiver;
        private readonly Rtl1Decoder sent;
        private byte expected;
        public readonly MemoryStream Received = new();
        public int EofCount;
        public int AbortCount;
        public string OpenCommand = "";
        public bool HoldCompletion;
        public TaskCompletionSource EofReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(string line) => receiver.Feed(Encoding.Latin1.GetBytes("[COM] " + line + "\n"));
        public Wire(Rtl1Decoder receiver)
        {
            this.receiver = receiver;
            sent = new Rtl1Decoder(Frame, Text);
        }
        private void Text(byte[] bytes)
        {
            string command = Encoding.Latin1.GetString(bytes);
            if (command.StartsWith("put ") && !command.StartsWith("put -a "))
            {
                OpenCommand = command;
                receiver.Feed(Encoding.Latin1.GetBytes("[COM] put ready nonce=0x1234\n"));
            }
            else if (command == "status\n") { receiver.Feed(Encoding.Latin1.GetBytes("[COM] status oerr=0\n")); }
            else { throw new Exception("Unexpected text on firmware wire: " + command); }
        }
        private void Frame(Rtl1Frame frame)
        {
            if (frame.Nonce != Nonce || frame.Seq != expected) { throw new Exception("Incorrect frame nonce or sequence."); }
            if (frame.Type == Rtl1Type.Abort)
            {
                AbortCount++;
                receiver.Feed(Encoding.Latin1.GetBytes("[COM] put aborted\n"));
                return;
            }
            if (frame.Type != Rtl1Type.Data) { throw new Exception("Unexpected upload frame type."); }
            expected++;
            Received.Write(frame.Payload);
            if (frame.Payload.Length == 0)
            {
                EofCount++;
                EofReceived.TrySetResult();
                if (!HoldCompletion) { Complete("put done"); }
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => sent.Feed(buffer.AsSpan(offset, count));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent.Feed(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Received.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
