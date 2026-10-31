namespace rt4k_pi;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

public partial class Program
{
    public static FactoryReset? Reset { get; private set; }

    private static void MapFactoryReset(WebApplication app)
    {
        var reset = new FactoryReset(Serial!, new SdCardCatalog(app.Services.GetRequiredService<HttpClient>()),
            app.Services.GetRequiredService<FirmwareCatalog>(), Firmware!, Path.Combine(AppContext.BaseDirectory, "factory-reset"));
        Reset = reset;

        app.MapGet("/FactoryReset/releases", async (HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            return Results.Json(await reset.GetReleasesAsync(timeout.Token), FactoryResetJsonContext.Default.FactoryResetReleases);
        });

        app.MapPost("/FactoryReset/start", (HttpContext context, [FromQuery] FactoryResetFirmware firmware, [FromQuery] bool confirmed, [FromQuery] bool fastChecks) =>
        {
            if (!FirmwareRequestAllowed(context) || !confirmed) { return Results.Text("A same-origin request and explicit confirmation are required.", statusCode: 400); }
            try
            {
                reset.Start(firmware, fastChecks);
                return Results.Accepted();
            }
            catch (InvalidOperationException ex) { return Results.Text(ex.Message, statusCode: 409); }
        });

        app.MapPost("/FactoryReset/cancel", (HttpContext context) =>
        {
            if (!FirmwareRequestAllowed(context)) { return Results.StatusCode(400); }
            return reset.Cancel() ? Results.Accepted() : Results.Text("It's too late to cancel.", statusCode: 409);
        });

        app.MapGet("/FactoryReset/stream", async context =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache,no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            CancellationToken token = context.RequestAborted;
            FactoryResetStatus? sent = null;
            int idle = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var current = reset.Status;
                    if (current != sent)
                    {
                        sent = current;
                        idle = 0;
                        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(current, FactoryResetJsonContext.Default.FactoryResetStatus) + "\n\n", token);
                        await context.Response.Body.FlushAsync(token);
                    }
                    else if ((idle += 250) >= KeepAliveMs)
                    {
                        idle = 0;
                        await context.Response.WriteAsync(": keepalive\n\n", token);
                        await context.Response.Body.FlushAsync(token);
                    }
                    await Task.Delay(250, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Page navigated away
            }
        });
    }
}
