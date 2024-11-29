namespace rt4k_pi;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using rt4k_pi.Slices;
using System.Reflection;

public partial class Program
{
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
        contentProvider.Mappings.Add(".avif", "image/avif"); // dotnet doesn't know about AVIF yet

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = embeddedProvider,
            ContentTypeProvider = contentProvider,
            //ServeUnknownFileTypes = true
        });

        var appState = new AppState()
        {
            Logger = logger,
            Serial = Serial,
            Ser2net = Ser2net,
            StatusDaemon = StatusDaemon,
            Settings = Settings,
            Installer = Installer
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
        app.MapGet("/", () => Results.Extensions.RazorSlice<Slices.Status, Slices.AppState>(appState));
        app.MapGet("/Remote", () => Results.Extensions.RazorSlice<Slices.Remote, Slices.AppState>(appState));
        app.MapGet("/Calculator", () => Results.Extensions.RazorSlice<Slices.Calculator, Slices.AppState>(appState));
        app.MapGet("/Settings/{cmd?}", ([FromRoute] string? cmd) => { appState.Command = cmd; return Results.Extensions.RazorSlice<Slices.Settings, Slices.AppState>(appState); });
        app.MapGet("/DebugLog", () => Results.Extensions.RazorSlice<Slices.DebugLog, Slices.AppState>(appState));

        // APIs
        app.MapGet("/GetUpdateStatus", () => Installer.GetStatus());
        app.MapGet("/CheckUpdates", () => Installer.CheckUpdate());

        // Commands
        app.MapGet("/SendSerial", ([FromQuery] string cmd) => Serial?.WriteLine(cmd));
        app.MapPost("/RemoteCommand/{cmd}", ([FromRoute] string cmd) => RT4K?.SendRemoteString(cmd) );
        app.MapPost("/UpdateSetting/{name}/{value}", ([FromRoute] string name, [FromRoute] string value) => Settings.UpdateSetting(name, value) );

        app.Run();
    }
}