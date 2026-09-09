namespace rt4k_pi;

using Microsoft.AspNetCore.Mvc;

public partial class Program
{
    private static readonly Lock updateStartLock = new();

    private sealed class SerialFirmwareDevice(Serial serial) : IFirmwareDevice
    {
        public bool IsConnected => serial.IsConnected;
        public long ConnectionRevision => serial.ConnectionRevision;
        public Task ExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken token) => serial.RunExclusiveAsync(action, token);
        public Task<List<string>> CommandAsync(string command, Func<string, bool> terminal, int timeoutMs, CancellationToken token) => serial.SendCommandAsync(command, terminal, timeoutMs, token);
        public Task UploadAsync(string path, Stream data, long length, string sha256, Action<long> progress, CancellationToken token) => serial.PutFirmwareFileAsync(path, data, length, sha256, progress, token);
    }

    private static void MapFirmware(WebApplication app)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var catalog = new FirmwareCatalog(http);
        var updater = new FirmwareUpdater(new SerialFirmwareDevice(Serial!), catalog, Path.Combine(AppContext.BaseDirectory, "firmware-update"));
        Firmware = updater;
        updater.StartCleanupMonitor();
        app.Lifetime.ApplicationStopped.Register(http.Dispose);

        app.MapGet("/Firmware/releases", async (HttpContext context, [FromQuery] bool? experimental, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var releases = await catalog.GetAsync(timeout.Token);
                return Results.Json(new FirmwareListing(releases.Where(r => experimental != false || !r.Experimental).ToArray()), FirmwareJsonContext.Default.FirmwareListing);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
            {
                return Results.Text("Could not load official firmware releases: " + ex.Message, statusCode: 503);
            }
        });

        app.MapGet("/Firmware/status", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            FirmwareProgress progress = updater.Status;
            var current = updater.Current;
            bool live = Serial?.IsConnected == true && RT4K?.Power == rt4k_pi.RT4K.PowerState.On;
            return Results.Json(new FirmwareView(progress, progress.Active ? current.Version : live ? RT4K?.Firmware : null, progress.Active ? current.Model : live ? RT4K?.Model : null, Serial?.IsConnected == true), FirmwareJsonContext.Default.FirmwareView);
        });

        app.MapPost("/Firmware/start", async (HttpContext context, [FromQuery] string id, [FromQuery] bool confirmed, CancellationToken token) =>
        {
            if (!FirmwareRequestAllowed(context) || !confirmed) { return Results.Text("A same-origin request and explicit confirmation are required.", statusCode: 400); }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var releases = await catalog.GetAsync(timeout.Token);
                FirmwareRelease? release = releases.SingleOrDefault(r => r.Id == id);
                if (release == null) { return Results.Text("The selected official release is unavailable. Refresh the release list.", statusCode: 400); }
                lock (updateStartLock)
                {
                    if (Installer.Updating) { return Results.Text("Wait for the rt4k_pi application update to finish.", statusCode: 409); }
                    updater.Start(release);
                }
                return Results.Accepted();
            }
            catch (InvalidOperationException ex) { return Results.Text(ex.Message, statusCode: 409); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { return Results.Text(ex.Message, statusCode: 503); }
        });

        app.MapPost("/Firmware/cancel", (HttpContext context) =>
        {
            if (!FirmwareRequestAllowed(context)) { return Results.StatusCode(400); }
            return updater.Cancel() ? Results.Accepted() : Results.Text("Cancellation is no longer available.", statusCode: 409);
        });
    }

    private static bool FirmwareRequestAllowed(HttpContext context)
    {
        if (context.Request.Headers["X-RT4K-Firmware"] != "1") { return false; }
        string? origin = context.Request.Headers.Origin;
        return string.IsNullOrEmpty(origin) || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority).Equals(context.Request.Scheme + "://" + context.Request.Host, StringComparison.OrdinalIgnoreCase));
    }
}
