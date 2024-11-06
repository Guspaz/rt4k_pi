using System.Runtime.InteropServices;
using rt4k_pi.Slices;

namespace rt4k_pi
{
    public class Program
    {
        const string VERSION = "1.0";

        public static Serial? Serial {get; private set;}

        public static void Main(string[] args)
        {
            Console.WriteLine($"rt4k_pi v{VERSION}");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Serial = new Serial(115200);
                var rt4k = new RT4K(Serial);
                var ser2net = new Ser2net(Serial, 2000);

                ser2net.Start();

                var memInfo = GetMemInfo();
            }

            var builder = WebApplication.CreateSlimBuilder(args);
            builder.WebHost.UseUrls("http://*:8080");

            var app = builder.Build();

            // Code here runs at startup, but the page code runs on load.
            // If we're being lazy/quick about it, just embed code in the pages.

            //app.MapGet("/", () => Results.Extensions.RazorSlice<Slices.Hello, (DateTime Time, Dictionary<string, string> MemInfo)>((Time: DateTime.Now, MemInfo: memInfo)));
            //app.MapGet("/Test1", () => Results.Extensions.RazorSlice<Slices.Test1, ViewState>(foo));
            //app.MapGet("/Test2", () => Results.Extensions.RazorSlice<Slices.Test2, ViewState>(foo));

            var appState = new AppState() { Serial = Serial };

            app.MapGet("/", () => Results.Extensions.RazorSlice<Slices.Status, Slices.AppState>(appState));

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