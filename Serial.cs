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

                var readTask = Task.Run(HandleRead, cts.Token);

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
                        Console.WriteLine($"Serial port error: {ex.Message}");
                        IsConnected = false;
                        //port.Close();
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
                        // Blocks until there's data, I think?
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
                    await Task.Delay(500);
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
