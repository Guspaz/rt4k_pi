using System.Runtime.InteropServices;

namespace rt4k_pi
{
    public partial class Program
    {
        public static readonly string VERSION = "1.0";

        public static Serial? Serial {get; private set;}
        public static RT4K? RT4K {get; private set;}

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
                var ser2net = new Ser2net(Serial, 2000);

                ser2net.Start();
            }

            RunWeb();
        }
    }
}