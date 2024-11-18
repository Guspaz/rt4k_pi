namespace rt4k_pi;

using System.Net;
using System.Net.Sockets;

public class Ser2net(Serial serial, int Port)
{
    private readonly TcpListener listener = new(IPAddress.Any, Port);
    private readonly CancellationTokenSource cts = new();

    public int Port { get; } = Port;

    public void Start()
    {
        Console.WriteLine("Initializing ser2net");

        listener.Start();

        Console.WriteLine($"Listening for connections on port {Port}");

        Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    Console.WriteLine("Client connected");
                    HandleClient(client);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting client: {ex.Message}");
                }
            }
        }, cts.Token);
    }

    public void Stop()
    {
        cts.Cancel();
        listener.Stop();
    }

    private void HandleClient(TcpClient client)
    {
        var networkStream = client.GetStream();

        async void reader(byte[] data)
        {
            try
            {
                if (client.Connected)
                {
                    await networkStream.WriteAsync(data, cts.Token);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending data to client: {ex.Message}");
                client.Close();
            }
        }

        serial.RegisterReader(reader);

        Task.Run(async () =>
        {
            var buffer = new byte[4096];

            try
            {
                while (client.Connected)
                {
                    var bytesRead = await networkStream.ReadAsync(buffer, cts.Token);
                    if (bytesRead == 0)
                    {
                        Console.WriteLine("Client disconnected");
                        break;
                    }

                    serial.Write(buffer[0..bytesRead]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error receiving data from client: {ex.Message}");
            }
            finally
            {
                serial.UnregisterReader(reader);
                client.Close();
            }
        });
    }
}
