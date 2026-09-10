namespace rt4k_pi;

public sealed class FirmwareCatalogRefresh(FirmwareCatalog catalog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                await catalog.GetAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
            {
                Console.WriteLine("Firmware release check: " + ex.Message);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
