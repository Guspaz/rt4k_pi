/*
 * TODO/ideas list
 * - Persistent settings support
 * - Ability to change SSID/password
 * - Some sort of SVS command support
 * - Pizza button
 * - First-run install support (register as service)
 * - Auto-update support
 * - Backup settings to RT4K (requires serial file IO)
 * - WebDAV server (requires serial file IO)
 * - RT4K automated firmware update (requires serial file IO, possibly firmware-related query and update commands)
 * - Web-based firmware renaming/management (requires serial file IO)
 * - Limited ANSI colour support for debug log
 * - Readme page
 * - Support for displaying messages (success/error) at the top of pages
 */

using System.Runtime.InteropServices;

namespace rt4k_pi
{
    public partial class Program
    {
        public static readonly string VERSION = "1.0";

        public static Serial? Serial {get; private set;}
        public static RT4K? RT4K {get; private set;}
        public static Ser2net? Ser2net {get; private set;}
        public static StatusDaemon StatusDaemon { get; private set; } = new();

        private static readonly Logger logger = new();

        public static void Main()
        {
            // Run all output through the debug log
            Console.SetOut(logger);
            
            Console.WriteLine($"rt4k_pi v{VERSION}");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Serial = new Serial(115200);
                RT4K = new RT4K(Serial);
                Ser2net = new Ser2net(Serial, 2000);

                Ser2net.Start();
            }

            RunWeb();
        }
    }
}

//[Unit]
//Description=rt4k_pi
//After=network.target
//StartLimitIntervalSec=0
//[Service]
//Type=simple
//Restart=always
//RestartSec=1
//ExecStart=/home/pi/rt4k

//[Install]
//WantedBy=multi-user.target