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
            catch (Exception ex)
            {
                // A failed check only means no update is offered; letting it escape would end
                // this service and, with it, the whole host.
                bool expected = ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException;
                Console.WriteLine("Firmware release check: " + (expected ? ex.Message : $"{ex.GetType().Name}: {ex.Message}"));
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
