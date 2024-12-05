/*
 * TODO/ideas list
 * - Checkboxes on settings page do something
 * - Ability to change SSID/password
 * - Some sort of SVS command support
 * - Pizza button
 * - Readme page
 * - Backup settings to RT4K (requires serial file IO)
 * - SMB/CIFS support (requires serial file IO)
 * - RT4K automated firmware update (requires serial file IO, possibly firmware-related query and update commands)
 * - Web-based firmware renaming/management (requires serial file IO)
 * - Better mobile experience
 * - Generally better error checking/handling/tolerance
 * - Look into generating minimal images with pi-gen-micro
 */

namespace rt4k_pi;

using System.Runtime.InteropServices;
using FuseDotNet;

public partial class Program
{
    public static readonly string VERSION = "1.0";

    public static Serial? Serial {get; private set;}
    public static RT4K? RT4K {get; private set;}
    public static Ser2net? Ser2net {get; private set;}
    public static StatusDaemon StatusDaemon { get; } = new();
    public static SettingsDaemon Settings { get; } = new();
    public static Installer Installer { get; } = new();

    private static readonly Logger logger = new();

    static Program()
    {
        // Ensure that we have the right working directory from the start (may get defaulted to root)
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
    }

    public static void Main()
    {
        Console.WriteLine("Starting up rt4k_pi");

        // Run all output through the debug log
        Console.SetOut(logger);
        
        Console.WriteLine($"rt4k_pi v{VERSION}\n");

        // We don't actually support Windows, but it's useful for testing.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Installer.CheckInstall();

            Serial = new Serial(115200);
            RT4K = new RT4K(Serial);
            Ser2net = new Ser2net(Serial, 2000);

            Ser2net.Start();

            Task.Run(() =>
            {
                try
                {
                    Console.WriteLine("Instantiating FUSE file system");
                    var fuseOp = new Filesystem.SerialFsOperations();
                    // TODO: This is running single threaded, should it? May be higher performance if not.
                    fuseOp.Mount(["rt4k_pi", "-s", "-d", "serialfs"], new FuseDotNet.Logging.ConsoleLogger());
                    Console.WriteLine("FUSE exited");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FUSE Error ({(ex is PosixException pex ? (int)pex.NativeErrorCode : ex.HResult)}): {ex.Message}");
                }
            });
        }

        Settings.Load();

        RunWeb();
    }
}

