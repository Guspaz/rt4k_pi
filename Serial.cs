using System.Diagnostics;
using System.Text;

namespace rt4k_pi
{
    // This is all terrible and can be massively improved.

    public class Serial
    {
        public bool IsConnected { get; private set; }

        private FileStream port = new("/dev/null", FileMode.Open);
        private readonly HashSet<Action<byte[]>> readers = [];
        private readonly Encoding encoding = Encoding.ASCII;
        private readonly CancellationTokenSource cts = new();

        public Serial(int baudRate)
        {
            Task.Run(async () =>
            {
                if (GetPort() == null)
                {
                    Console.WriteLine("Serial port does not exist, waiting for connection.");
                }

                while (!cts.Token.IsCancellationRequested)
                {
                    try
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

                            // Run the read loop until aborted
                            await Task.Run(HandleRead, cts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Serial port error: {ex.Message}. Retrying in 2 seconds...");
                    }

                    IsConnected = false;
                    await Task.Delay(2000); // Wait before retrying
                }
            }, cts.Token);

            // Monitor connection status
            Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(2000);

                    if (IsConnected && !File.Exists(port.Name))
                    {
                        Console.WriteLine("Serial port disconnected.");
                        port.Close();
                    }
                }
            }, cts.Token);
        }

        ~Serial()
        {
            cts.Cancel();
            port.Close();
        }

        private static string? GetPort() => Directory.GetFiles("/dev", "ttyUSB*").FirstOrDefault();

        private static void ConfigurePort(string portName, int baudRate)
        {
            using (Process? process = Process.Start(
                new ProcessStartInfo {
                    FileName = "stty",
                    Arguments = $"-F {portName} {baudRate} cs8 -cstopb -parenb",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true }))
            {
                if (null == process)
                {
                    throw new IOException($"Failed to configure serial port: unable to start stty");
                }

                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd();
                    throw new IOException($"Failed to configure serial port: {error}");
                }
            }
        }

        private void HandleRead()
        {
            Console.WriteLine("Starting serial read loop");
            byte[] readBuf = new byte[4096];
            while (!cts.Token.IsCancellationRequested)
            {
                // Blocks until there's data, I think?
                int read = port.Read(readBuf, 0, readBuf.Length);

                if (read > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write(encoding.GetString(readBuf, 0, read));
                    Console.ResetColor();

                    foreach (Action<byte[]> action in readers)
                    {
                        action(readBuf[0..read]);
                    }
                }
            }
        }

        public void RegisterReader(Action<byte[]> reader) => readers.Add(reader);

        public void UnregisterReader(Action<byte[]> reader) => readers.Remove(reader);

        public void WriteLine(string data)
        {
            this.Write(encoding.GetBytes(data + '\n'));
        }

        // TODO: Make this async somehow
        public void Write(byte[] data)
        {
            if (IsConnected)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.Write(encoding.GetString(data));
                Console.ResetColor();

                port.Write(data);
                port.Flush();
                // TODO: Do we need this flush here?
            }
        }
    }
}
