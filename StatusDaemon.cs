using System.Runtime.InteropServices;

namespace rt4k_pi
{
    public class StatusDaemon
    {
        public Dictionary<string, string> WifiStatus { get; private set; } = new Dictionary<string, string> { { "active", "no" }, { "ssid", "" } };
        public string Hostname { get; private set; } = "localhost";

        private readonly CancellationTokenSource cts = new();

        private const int UPDATE_INTERVAL = 10 * 1000;

        public StatusDaemon()
        {
            Task.Run(async () =>
            {
                Hostname = Util.RunCommand("hostname").ToLower();

                try
                {
                    string[] fields = Util.RunCommand("nmcli", "-t -c no -f active,ssid,chan,rate,signal dev wifi").Split('\n').Where(l => l.StartsWith("yes:")).First().Split(':');

                    WifiStatus = new Dictionary<string, string>
                    {
                        {"active", fields[0]},
                        {"ssid", fields[1]},
                        {"chan", fields[2]},
                        {"rate", fields[3]},
                        {"signal", fields[4]},
                        {"quality", Convert.ToInt32(fields[4]) switch { > 80 => "great", > 55 => "good", > 30 => "weak", > 5 => "bad", _ => "terrible" } }
                    };
                }
                catch
                {
                    WifiStatus = new Dictionary<string, string> { { "active", "no" }, { "ssid", "" } };
                }

                await Task.Delay(UPDATE_INTERVAL, cts.Token);
            }, cts.Token);
        }
    }
}
