using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using rt4k_pi.Slices;
using System.Reflection;

namespace rt4k_pi
{
    public partial class Program
    {
        public static void RunWeb()
        {
            var allowedHosts = new Dictionary<string, string?>
            {
                { "AllowedHosts", "example.com;localhost" }
            };

            var builder = WebApplication.CreateSlimBuilder();
            builder.Configuration.Sources.Clear(); // Disable appsettings
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { { "AllowedHosts", "*" } });
            builder.WebHost.UseUrls("http://*:80");

            var app = builder.Build();

            var embeddedProvider = new EmbeddedFileProvider(Assembly.GetExecutingAssembly(), "rt4k_pi");

            var contentProvider = new FileExtensionContentTypeProvider();
            contentProvider.Mappings.Add(".avif", "image/avif");

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = embeddedProvider,
                ContentTypeProvider = contentProvider,
                //ServeUnknownFileTypes = true
            });

            var appState = new AppState() { Logger = logger, Serial = Serial };

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
            app.MapPost("/RemoteCommand/{cmd}", ([FromRoute] string cmd) => { RT4K?.SendRemoteString(cmd); });
            app.MapGet("/DebugLog", () => Results.Extensions.RazorSlice<Slices.DebugLog, Slices.AppState>(appState));

            app.Run();
        }
    }
}
