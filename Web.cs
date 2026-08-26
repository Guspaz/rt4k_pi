namespace rt4k_pi;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using rt4k_pi.Slices;
using System.Reflection;

public partial class Program
{
    // How long an idle OSD stream goes before writing a keep-alive. Doubles as how quickly a
    // closed tab is noticed: an aborted connection only surfaces when we try to write to it.
    private const int KeepAliveMs = 3000;

    public static void RunWeb()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear(); // Disable appsettings
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { { "AllowedHosts", "*" } });
        builder.WebHost.UseUrls("http://*:80");

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = false;
            options.SingleLine = true;
            options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled;
        });

        var app = builder.Build();

        var embeddedProvider = new EmbeddedFileProvider(Assembly.GetExecutingAssembly(), "rt4k_pi");

        var contentProvider = new FileExtensionContentTypeProvider();

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = embeddedProvider,
            ContentTypeProvider = contentProvider,
            ServeUnknownFileTypes = true
        });

        var appState = new AppState()
        {
            Logger = logger,
            Serial = Serial,
            Ser2net = Ser2net,
            StatusDaemon = StatusDaemon,
            FuseDaemon = FuseDaemon,
            Settings = Settings,
            Installer = Installer,
            RT4K = RT4K
        };

        var assembly = Assembly.GetExecutingAssembly();

        // Retrieve and print all embedded resource names
        Console.WriteLine("Embedded resources:");
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            Console.WriteLine(resourceName);
        }

        // Static file overrides
        app.MapGet("/favicon.ico", () => Results.File(embeddedProvider.GetFileInfo("Static/favicon.ico").CreateReadStream(), "image/x-icon"));

        // Pages
        app.MapGet("/", () => Results.RazorSlice<Slices.Status, Slices.AppState>(appState));
        app.MapGet("/RemoteOSD", () => Results.RazorSlice<Slices.RemoteOSD, Slices.AppState>(appState));
        app.MapGet("/Calculator", () => Results.RazorSlice<Slices.Calculator, Slices.AppState>(appState));
        app.MapGet("/Settings", () => Results.RazorSlice<Slices.Settings, Slices.AppState>(appState));
        app.MapGet("/DebugLog", () => Results.RazorSlice<Slices.DebugLog, Slices.AppState>(appState));

        // APIs
        app.MapGet("/GetUpdateStatus", () => Installer.GetStatus());
        app.MapGet("/CheckUpdates", () => Installer.CheckUpdate());

        // Commands
        app.MapGet("/SendSerial", ([FromQuery] string cmd) => Serial?.WriteLine(cmd));

        // Kept for the page's first paint and as a no-JS fallback; live updates arrive on
        // /OsdStream instead of by polling this.
        app.MapGet("/OsdImage", (HttpContext context) =>
        {
            if (RT4K is null)
            {
                context.Response.Headers["X-Osd-State"] = nameof(OsdMirror.OsdState.Disconnected);
                return Results.NoContent();
            }

            byte[]? png = RT4K.Osd.CurrentPng;
            context.Response.Headers["X-Osd-State"] = RT4K.Osd.State.ToString();

            return png is null ? Results.NoContent() : Results.Bytes(png, "image/png");
        });

        // Client heartbeat: the page calls this while it's visible to keep the mirror capturing.
        // A lease is used rather than the SSE connection's own lifetime because a navigated-away
        // tab leaves its socket looking healthy for tens of seconds, during which small writes
        // still succeed and RequestAborted never fires.
        app.MapPost("/OsdWatch", () => RT4K?.Osd.Watch());

        // Server-sent events: the mirror pushes a frame whenever it actually changes, so the
        // browser needs no polling, no rate limiting and no post-keypress refresh logic.
        app.MapGet("/OsdStream", async (HttpContext context) =>
        {
            if (RT4K is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache,no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            CancellationToken token = context.RequestAborted;
            OsdMirror osd = RT4K.Osd;

            long sent = -1;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Read the revision first: if a capture lands between these two reads we'd
                    // rather re-send the same frame next pass than record a revision we never sent.
                    long revision = osd.Revision;
                    byte[]? png = osd.CurrentPng;

                    if (revision != sent)
                    {
                        sent = revision;

                        // Inlined as a data URI: the frame is small, and this keeps the image and
                        // the state it belongs to in a single atomic message.
                        string image = png is null ? "" : Convert.ToBase64String(png);

                        await context.Response.WriteAsync($"data: {{\"state\":\"{osd.State}\",\"image\":\"{image}\"}}\n\n", token);
                    }
                    else
                    {
                        // A comment is ignored by EventSource, so an idle stream stays open
                        // without re-sending a frame the page already has.
                        await context.Response.WriteAsync(": keepalive\n\n", token);
                    }

                    await context.Response.Body.FlushAsync(token);

                    // Blocks until the mirror actually captures something different, so a
                    // keypress reaches the browser as soon as the frame exists.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(KeepAliveMs);

                    try
                    {
                        await osd.WaitForChangeAsync(sent, idle.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // Nothing changed for a while; loop round to emit a keep-alive
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Page navigated away
            }
        });

        app.MapPost("/RemoteCommand/{cmd}", async ([FromRoute] string cmd) =>
        {
            if (RT4K is not null)
            {
                await RT4K.SendRemoteStringAsync(cmd);
            }
        });

        app.MapPost("/UpdateSetting/{name}/{value}", ([FromRoute] string name, [FromRoute] string value) => Settings.UpdateSetting(name, value) );
        app.MapPost("/InstallUpdate", () => Installer.DoUpdate());
        app.MapPost("/RunBenchmark", async () => RT4K is null ? "RT4K not available" : await RT4K.BenchmarkAsync());

        Console.WriteLine("rt4k_pi startup complete.");

        app.Run();
    }
}