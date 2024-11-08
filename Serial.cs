﻿using System.Diagnostics;
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

        public Serial(int baudRate)
        {
            Task.Run(async () =>
            {
                if (GetPort() == null)
                {
                    Console.WriteLine("Serial port does not exist, waiting for connection.");
                }

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

        private static void ConfigurePort(string portName, int baudRate) =>
            Util.RunCommand("stty", $"-F {portName} {baudRate} cs8 -cstopb -parenb");

        private void HandleWrite()
        {
            while (!cts.IsCancellationRequested)
            {
                if (IsConnected && writeQueue.Count > 0)
                {
                    while (writeQueue.Count > 0)
                    {
                        byte[] data = writeQueue.Dequeue();
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        Console.Write(encoding.GetString(data));
                        Console.ResetColor();

                        port.Write(data);
                        port.Flush(); // TODO: Do we need this flush here?
                    }
                }

                Thread.Sleep(0);
            }
        }

        private void HandleRead()
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
                    Thread.Sleep(0);
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
                // TODO: Add a limit here and block if the queue is full?
                writeQueue.Enqueue(data);
            }
        }
    }
}
