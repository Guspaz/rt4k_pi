using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.FileProviders;
using rt4k_pi.Slices;

namespace rt4k_pi
{
    public class Program
    {
        const string VERSION = "1.0";

        public static Serial? Serial {get; private set;}

        public static void Main(string[] args)
        {
            // Run all output through the debug log
            var logger = new Logger();
            Console.SetOut(logger);
            
            Console.WriteLine($"rt4k_pi v{VERSION}");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Serial = new Serial(115200);
                var rt4k = new RT4K(Serial);
                var ser2net = new Ser2net(Serial, 2000);

                ser2net.Start();

                var memInfo = GetMemInfo();
            }

            var allowedHosts = new Dictionary<string, string?>
            {
                { "AllowedHosts", "example.com;localhost" }
            };

            var builder = WebApplication.CreateSlimBuilder(args);
            builder.Configuration.Sources.Clear(); // Disable appsettings
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { { "AllowedHosts", "*" } });
            builder.WebHost.UseUrls("http://*:80");

            var embeddedProvider = new EmbeddedFileProvider(Assembly.GetExecutingAssembly(), "rt4k_pi");

            var app = builder.Build();

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = embeddedProvider
            });

            var appState = new AppState() { Logger = logger, Serial = Serial };

            // Static file overrides
            app.MapGet("/favicon.ico", () => Results.File(embeddedProvider.GetFileInfo("Static/favicon.ico").CreateReadStream(), "image/x-icon"));

            // Pages
            app.MapGet("/", () => Results.Extensions.RazorSlice<Slices.Status, Slices.AppState>(appState));
            app.MapGet("/DebugLog", () => Results.Extensions.RazorSlice<Slices.DebugLog, Slices.AppState>(appState));

            app.Run();
        }

        private static Dictionary<string, string> GetMemInfo() =>
            new(
                File.ReadLines("/proc/meminfo")
                    .Select(l => l.Split(':', 2, StringSplitOptions.TrimEntries))
                    .ToDictionary(p => p[0], p => p[1])
            );
    }
}