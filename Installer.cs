using System.Text;

namespace rt4k_pi
{
    public class Installer
    {
        public string systemd;

        private const string path = "/etc/systemd/system/rt4k.service";

        public Installer()
        {
            StringBuilder sb = new();
            sb.AppendLine("[Unit]");
            sb.AppendLine("Description=rt4k_pi");
            sb.AppendLine("After=network.target");
            sb.AppendLine("StartLimitIntervalSec=0");
            sb.AppendLine("[Service]");
            sb.AppendLine("Type=simple");
            sb.AppendLine("Restart=always");
            sb.AppendLine("RestartSec=1");
            sb.AppendLine($"ExecStart={Directory.GetCurrentDirectory()}/rt4k_pi");
            sb.AppendLine("");
            sb.AppendLine("[Install]");
            sb.AppendLine("WantedBy=multi-user.target");
            systemd = sb.ToString();
        }

        public void DoInstall()
        {
            Console.WriteLine("Ensuring SystemD service is installed");
            File.WriteAllText(path, systemd);

            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INVOCATION_ID")))
            {
                Console.WriteLine("Not running as a service, starting SystemD service");
                Util.RunCommand("systemctl", "enable rt4k");
                Util.RunCommand("systemctl", "daemon-reload");
                Util.RunCommand("systemctl", "restart rt4k");
                Console.WriteLine("Quitting to let SystemD take over");
                Environment.Exit(0);
            }
            else
            {
                Console.WriteLine("Already running under SystemD");
            }
        }
    }
}
