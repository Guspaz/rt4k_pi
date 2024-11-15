﻿using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace rt4k_pi
{
    public class Installer
    {
        public string systemd;

        private const string path = "/etc/systemd/system/rt4k.service";

        public bool Updating { get; private set; } = false;
        public int UpdateProgress { get; private set; } = 0;
        public string UpdateError { get; private set; } = "";

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

        public void CheckInstall()
        {
            Console.WriteLine("Ensuring SystemD service is installed");
            File.WriteAllText(path, systemd);

            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INVOCATION_ID")))
            {
                Console.WriteLine("Not running as a service, starting SystemD service");
                DoInstall();
            }
            else
            {
                Console.WriteLine("Already running under SystemD");
            }
        }

        private static void DoInstall()
        {
            Util.RunCommand("systemctl", "enable rt4k");
            Util.RunCommand("systemctl", "daemon-reload");
            Util.RunCommand("systemctl", "restart rt4k");
            Console.WriteLine("Quitting to update SystemD service");
            Environment.Exit(0);
        }

        public static string CheckUpdate()
        {
            try
            {
                return new HttpClient().GetStringAsync("https://guspaz.github.io/rt4k.version").Result.Split('@')[0].Trim();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in CheckUpdate: {ex.Message}");
                return "";
            }
        }

        public void DoUpdate()
        {
            Updating = true;

            Console.WriteLine("Update triggered");

            Task.Run(async () =>
            {
                try
                {
                    var updateInfo = (await new HttpClient().GetStringAsync("https://guspaz.github.io/rt4k.version")).Split('@');
                    var downloadUrl = updateInfo[1].Trim();
                    var downloadHash = updateInfo[2].Trim();

                    Console.WriteLine($"Downloading update from {downloadUrl}");

                    var download = await new HttpClient().GetAsync(downloadUrl);

                    long length = download.Content.Headers.ContentLength ?? 0;

                    if (length == 0)
                    {
                        SetUpdateError("Invalid update size");
                        return;
                    }

                    Console.WriteLine($"Download size: {length} bytes");

                    using (var downloadStream = await download.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream("updateFile", FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[8192];
                        int bytesRead;
                        long totalBytesRead = 0;

                        while ((bytesRead = await downloadStream.ReadAsync(buffer)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                            sha256.AppendData(buffer, 0, bytesRead);
                            totalBytesRead += bytesRead;

                            UpdateProgress = (int)((double)totalBytesRead / length * 100);
                        }

                        var hash = Convert.ToHexStringLower(sha256.GetHashAndReset());
                        if (hash != downloadHash)
                        {
                            SetUpdateError("Update hash mismatch");
                            return;
                        }
                        else
                        {
                            Console.WriteLine("Download succesful, update hash matches");
                        }

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            Console.WriteLine("Replacing executable");
                            File.Move("updateFile", "rt4k_pi", true);
                            Console.WriteLine("Restarting service with new executable");
                            DoInstall();
                        }

                        UpdateProgress = 100;
                        UpdateError = "";
                        Updating = false;
                    }
                }
                catch (Exception ex)
                {
                    SetUpdateError($"Update error: {ex.Message}");
                }
            });
        }

        private void SetUpdateError(string error)
        {
            UpdateError = error;
            UpdateProgress = 0;
            Updating = false;
        }
    }
}
