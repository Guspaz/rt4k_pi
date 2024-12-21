namespace rt4k_pi;

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

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

    public string GetStatus()
    {
        if (Updating)
        {
            return $"{UpdateProgress}%";
        }
        else if (!string.IsNullOrWhiteSpace(UpdateError))
        {
            return UpdateError;
        }

        return "Idle";
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
            return Program.Settings.LatestVersion = new HttpClient().GetStringAsync("https://guspaz.github.io/rt4k.version").Result.Split('@')[0].Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in CheckUpdate: {ex.Message}");
            return "";
        }
    }

    public void DoUpdate()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            SetUpdateError("Unable to update on Windows");
            return;
        }
        else if (Updating)
        {
            return;
        }

        // For some reason, this isn't set?
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

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

                using var downloadStream = await download.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream("updateFile.7z", FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    
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

                await fileStream.FlushAsync();
                        
                Console.WriteLine("Download succesful, update hash matches");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Console.WriteLine("Extracting update");
                    Util.RunCommand("7zr", "x -y updateFile.7z");
                    Util.RunCommand("chmod", "+x rt4k_pi");
                    Console.WriteLine("Restarting service with new executable");
                    DoInstall();
                }

                UpdateProgress = 100;
                UpdateError = "";
                Updating = false;
                    
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

    public bool IsSambaInstalled()
    {
        try
        {
            Console.WriteLine("Checking if Samba is installed...");

            string result = Util.RunCommand("dpkg", "-l samba");
            if (result.Contains("ii  samba")) // "ii" indicates installed packages
            {
                Console.WriteLine("Samba is installed.");
                return true;
            }
        }
        catch { }

        Console.WriteLine("Samba is not installed.");
        return false;
    }

    public bool EnsureSambaInstalled()
    {
        try
        {
            if (IsSambaInstalled())
            {
                return true;
            }

            Console.WriteLine("Installing Samba");

            // Update package list
            Util.RunCommand("sudo", "apt-get update");

            // Install Samba
            Util.RunCommand("sudo", "apt-get install -y samba");

            Console.WriteLine("Samba installation complete.");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error ensuring Samba is installed: {ex.Message}");
        }

        return false;
    }

    public bool EnsureSambaConfig()
    {
        string configFilePath = "/etc/samba/smb.conf";

        // Define the new share configuration
        StringBuilder sb = new();
        sb.AppendLine("[global]");
        sb.AppendLine("   map to guest = Bad User");
        sb.AppendLine("   guest account = root");
        sb.AppendLine("   browseable = yes");
        sb.AppendLine("");
        sb.AppendLine("[sd]");
        sb.AppendLine($"   path = {Directory.GetCurrentDirectory()}/serialfs");
        sb.AppendLine("   browseable = yes");
        sb.AppendLine("   writable = yes");
        sb.AppendLine("   guest ok = yes");
        sb.AppendLine("   guest only = yes");
        sb.AppendLine("   create mask = 0777");
        sb.AppendLine("   directory mask = 0777");

        try
        {
            // Write the new configuration to the file
            File.WriteAllText(configFilePath, sb.ToString());
            Console.WriteLine("Samba configuration file replaced with new configuration.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
        }

        return false;
    }
}