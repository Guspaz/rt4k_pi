/*
 * TODO/ideas list
 * - Ability to change SSID/password
 * - Some sort of SVS command support
 * - Pizza button
 * - Auto-update support
 * - Backup settings to RT4K (requires serial file IO)
 * - WebDAV server (requires serial file IO)
 * - RT4K automated firmware update (requires serial file IO, possibly firmware-related query and update commands)
 * - Web-based firmware renaming/management (requires serial file IO)
 * - Readme page
 * - Support for displaying messages (success/error) at the top of pages
 * - Real power toggling with remote (requires RT4K "get pwr" type command)
 * - Better mobile experience
 */

using System.Runtime.InteropServices;
using FuseDotNet;

namespace rt4k_pi
{
    public partial class Program
    {
        public static readonly string VERSION = "1.0";

        public static Serial? Serial {get; private set;}
        public static RT4K? RT4K {get; private set;}
        public static Ser2net? Ser2net {get; private set;}
        public static StatusDaemon StatusDaemon { get; private set; } = new();
        public static SettingsDaemon Settings { get; private set; } = new();

        private static readonly Logger logger = new();

        public static void Main()
        {
            // Run all output through the debug log
            Console.SetOut(logger);
            
            Console.WriteLine($"rt4k_pi v{VERSION}\n");

            // We don't actually support Windows, but it's useful for testing.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                new Installer().DoInstall();

                Serial = new Serial(115200);
                RT4K = new RT4K(Serial);
                Ser2net = new Ser2net(Serial, 2000);

                Ser2net.Start();

                //Task.Run(() =>
                //{
                //    try
                //    {
                //        Console.WriteLine("Instantiating FUSE file system");
                //        var fuseOp = new SerialFsOperations();
                //        //fuseOp.Mount(["-s", "-d", "serialfs"]);
                //        fuseOp.Mount(["--help"], new FuseDotNet.Logging.ConsoleLogger());
                //        Console.WriteLine("FUSE exited");
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine($"FUSE Error ({(ex is PosixException pex ? (int)pex.NativeErrorCode : ex.HResult)}): {ex.Message}");
                //    }
                //});
            }

            Settings.Load();

            RunWeb();
        }
    }
}

