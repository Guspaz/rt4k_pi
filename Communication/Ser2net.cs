namespace rt4k_pi;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

public class Ser2net(Serial serial, int Port) : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? cts;
    private TcpListener? listener;
    private Task? acceptLoop;

    public int Port { get; } = Port;

    public void Start()
    {
        lifecycle.Wait();
        try
        {
            if (acceptLoop is { IsCompleted: false })
            {
                return;
            }

            listener?.Stop();
            cts?.Dispose();
            cts = null;
            acceptLoop = null;
            listener = null;

            var server = new TcpListener(IPAddress.Any, Port);
            try
            {
                server.Start();
            }
            catch
            {
                server.Stop();
                throw;
            }

            listener = server;
            cts = new CancellationTokenSource();
            acceptLoop = AcceptClientsAsync(listener, cts);
            Console.WriteLine($"Listening for text commands on port {Port}");
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (cts == null)
            {
                return;
            }

            cts.Cancel();
            listener?.Stop();
            if (acceptLoop != null)
            {
                await acceptLoop;
            }

            cts.Dispose();
            cts = null;
            listener = null;
            acceptLoop = null;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptClientsAsync(TcpListener server, CancellationTokenSource lifetime)
    {
        var clients = new List<Task>();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                TcpClient client = await server.AcceptTcpClientAsync(lifetime.Token);
                clients.RemoveAll(task => task.IsCompleted);
                clients.Add(HandleClientAsync(client, lifetime.Token));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested)
            {
                Console.WriteLine($"Error accepting client: {ex.Message}");
            }
        }
        finally
        {
            lifetime.Cancel();
            server.Stop();
            await Task.WhenAll(clients);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var ownedClient = client;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        NetworkStream stream = client.GetStream();
        var outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var callbackGate = new Lock();
        bool acceptingLines = true;

        void Reader(string line)
        {
            lock (callbackGate)
            {
                if (acceptingLines && !outgoing.Writer.TryWrite(line))
                {
                    lifetime.Cancel();
                }
            }
        }

        async Task SendAsync()
        {
            try
            {
                await foreach (string line in outgoing.Reader.ReadAllAsync(lifetime.Token))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await stream.WriteAsync(Encoding.Latin1.GetBytes(line + "\r\n"), timeout.Token);
                }
            }
            finally
            {
                lifetime.Cancel();
            }
        }

        serial.RegisterReader(Reader);
        Task sending = SendAsync();
        try
        {
            var buffer = new byte[4096];
            var command = new StringBuilder();
            while (true)
            {
                int count = await stream.ReadAsync(buffer, lifetime.Token);
                if (count == 0)
                {
                    break;
                }

                for (int i = 0; i < count; i++)
                {
                    byte value = buffer[i];
                    if (value is 13 or 10)
                    {
                        if (command.Length == 0)
                        {
                            continue;
                        }

                        string text = command.ToString();
                        command.Clear();
                        try
                        {
                            await serial.SendTextCommandAsync(text, lifetime.Token);
                        }
                        catch (Exception ex) when (ex is ArgumentException or SerialException)
                        {
                            Reader($"[HOST] {ex.Message}");
                        }
                    }
                    else if (value < 32 || value > 126 || command.Length == Serial.MaxCommandLength)
                    {
                        throw new IOException("Invalid or oversized text command; closing client.");
                    }
                    else
                    {
                        command.Append((char)value);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine($"TCP client error: {ex.Message}");
        }
        finally
        {
            // A serial snapshot may still contain Reader after it is unregistered.
            lock (callbackGate)
            {
                acceptingLines = false;
            }
            serial.UnregisterReader(Reader);
            lifetime.Cancel();
            outgoing.Writer.TryComplete();
            try { await sending; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"TCP send error: {ex.Message}"); }
        }
    }
}
