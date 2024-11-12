using System.Diagnostics;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace rt4k_pi
{
    // This is all terrible and can be massively improved.

    // TODO: Implement a max size for the write queue, we don't want to queue up an entire big file in one go.

    public class Serial
    {
        public bool IsConnected { get; private set; }

        private FileStream port = new("/dev/null", FileMode.Open);
        private readonly HashSet<Action<byte[]>> readers = [];
        private readonly Encoding encoding = Encoding.ASCII;
        private readonly CancellationTokenSource cts = new();
        private readonly Queue<byte[]> writeQueue = new();
        private TaskCompletionSource writeCompletion = new();
        private int writeQueueLength = 0;

        private bool fileModeRequested = false;
        private bool fileModeActive = false;

        private readonly int maxQueueLength = 8 * 1024 * 1024; // 8MB write buffer

        public Serial(int baudRate)
        {
            Task.Run(async () =>
            {
                if (GetPort() == null)
                {
                    Console.WriteLine("Serial port does not exist, waiting for connection.");
                }

                // TODO: These are async functions, do they even need a task?
                var readTask = Task.Run(HandleRead, cts.Token);
                var writeTask = Task.Run(HandleWrite, cts.Token);

                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (IsConnected && !File.Exists(port.Name))
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
                                ConfigurePort(currentPort, baudRate);
                                port = new FileStream(currentPort, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                                Console.WriteLine($"Connected to {currentPort}");
                                IsConnected = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Serial error: {ex.Message}");
                        IsConnected = false;
                        port.Dispose();
                    }

                    await Task.Delay(2000);
                }
            }, cts.Token);
        }

        ~Serial()
        {
            cts.Cancel();
            port.Close();
        }

        private static string? GetPort() => Directory.GetFiles("/dev", "ttyUSB*").FirstOrDefault();

        private static void ConfigurePort(string portName, int baudRate) =>
            Util.RunCommand("stty", $"-F {portName} {baudRate} cs8 -cstopb -parenb");

        private async void HandleWrite()
        {
            while (!cts.IsCancellationRequested)
            {
                // Wait until we're notified there's data to write
                await writeCompletion.Task.WaitAsync(cts.Token);
                writeCompletion = new TaskCompletionSource();

                // Handle entering/exiting file mode
                if (fileModeRequested && !fileModeActive)
                {
                    fileModeActive = true;
                }
                else if (fileModeActive && !fileModeRequested)
                {
                    fileModeActive = false;
                }

                while (IsConnected && !fileModeActive && writeQueue.Count > 0)
                {
                    byte[] data = writeQueue.Dequeue();
                    writeQueueLength -= data.Length;
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.Write(encoding.GetString(data));
                    Console.ResetColor();

                    try
                    {
                        port.Write(data);
                        port.Flush(); // TODO: Do we need this flush here?
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Serial error: {ex.Message}");
                        IsConnected = false;
                        port.Dispose();
                    }
                }
            }
        }

        private async void EnterFileMode()
        {
            // Request entering file mode and wake the write thread to acknowledge it
            fileModeRequested = true;
            writeCompletion.SetResult();

            // Wait for file mode to activate
            while (!fileModeActive)
            {
                await Task.Delay(1);
            }
        }

        private async void ExitFileMode()
        {
            // Request exiting file mode and wake the write thread to acknowledge it
            fileModeRequested = false;
            writeCompletion.SetResult();

            // Wait for file mode to deactivate
            while (fileModeActive)
            {
                await Task.Delay(1);
            }
        }

        private async void HandleRead()
        {
            Console.WriteLine("Starting serial read loop");
            byte[] readBuf = new byte[4096];
            while (!cts.Token.IsCancellationRequested)
            {
                if (IsConnected)
                {
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
                    }

                    if (read > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write(encoding.GetString(readBuf, 0, read));
                        Console.ResetColor();

                        foreach (Action<byte[]> action in readers)
                        {
                            try
                            {
                                action(readBuf[0..read]);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Warning: error calling registered reader: {ex.Message}");
                            }
                        }
                    }
                }
                else
                {
                    await Task.Delay(100);
                }
            }
        }

        public void RegisterReader(Action<byte[]> reader) => readers.Add(reader);

        public void UnregisterReader(Action<byte[]> reader) => readers.Remove(reader);

        public void WriteLine(string data)
        {
            this.Write(encoding.GetBytes(data + '\n'));
        }

        // TODO: If we're in file mode, offer a "read" function
        public void Write(byte[] data)
        {
            if (IsConnected)
            {
                if (fileModeActive)
                {
                    // If file mode is active, bypass the write queue and just let the stream handle the throttling
                    // TODO: Make this async
                    port.Write(data);
                }
                else
                {
                    while (writeQueueLength >= maxQueueLength)
                    {
                        if (!IsConnected)
                        {
                            // If we lose the connection, just drop the data 
                            return;
                        }

                        Thread.Sleep(1);
                    }

                    writeQueue.Enqueue(data);

                    writeQueueLength += data.Length;

                    // Wake up the write handler
                    writeCompletion.TrySetResult();
                }
            }
        }
    }
}
