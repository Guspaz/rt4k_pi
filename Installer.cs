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

    // Package operations pull from the network and unpack onto an SD card, so the default
    // command timeout is nowhere near enough for them
    private const int PackageTimeoutMs = 300000;

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
        // A FUSE mount can leave the process unresponsive to SIGTERM, and the default here is a
        // 90 second wait before systemd resorts to SIGKILL. That stall is what a restart ends up
        // sitting on, so cut it short: there is no shutdown work worth waiting that long for.
        sb.AppendLine("TimeoutStopSec=10");
        sb.AppendLine($"ExecStart={Directory.GetCurrentDirectory()}/rt4k_pi");
        sb.AppendLine("");
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=multi-user.target");
        systemd = sb.ToString();
    }

    public void CheckInstall()
    {
        try
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
        catch (Exception ex)
        {
            // Not being able to install the service is no reason to refuse to run: carry on and
            // let the user see the problem in the web UI rather than dying before it comes up
            Console.WriteLine($"Error installing the SystemD service: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns off wifi power saving. The Pi Zero 2 W's radio parks itself between beacons by
    /// default, which costs both throughput and a lot of latency, and everything this app does
    /// (the web UI, the OSD mirror, the SMB share) is small-request round trips that feel it.
    /// The setting does not survive a reboot or a reconnect, so it is reapplied on every start.
    /// </summary>
    public static void DisableWifiPowerSave(string device = "wlan0")
    {
        try
        {
            if (!Directory.Exists($"/sys/class/net/{device}"))
            {
                Console.WriteLine($"No {device} interface, leaving wifi power saving alone");
                return;
            }

            Util.RunElevated($"iw dev {device} set power_save off");
            Console.WriteLine($"Disabled wifi power saving on {device}");
        }
        catch (Exception ex)
        {
            // Wired, or a driver that doesn't support the call: not worth failing startup over
            Console.WriteLine($"Could not disable wifi power saving: {ex.Message}");
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
        Util.RunElevated("systemctl enable rt4k");
        Util.RunElevated("systemctl daemon-reload");

        // --no-block: restarting the unit means stopping the copy of ourselves that is already
        // running under it, and systemd waits for that stop job (up to its 90 second timeout)
        // before the restart command returns. We're about to exit anyway and don't care about
        // the result, so queue the job and get out of the way rather than sitting on it.
        Util.RunElevated("systemctl restart --no-block rt4k");

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

    public bool IsKsmbdInstalled()
    {
        try
        {
            Console.WriteLine("Checking if ksmbd is installed...");

            string result = Util.RunCommand("dpkg", "-l ksmbd-tools");
            if (result.Contains("ii  ksmbd-tools")) // "ii" indicates installed packages
            {
                Console.WriteLine("ksmbd is installed.");
                return true;
            }
        }
        catch { }

        Console.WriteLine("ksmbd is not installed.");
        return false;
    }

    public bool EnsureKsmbdInstalled()
    {
        try
        {
            if (IsKsmbdInstalled())
            {
                return true;
            }

            Console.WriteLine("Installing ksmbd");
            Util.RunElevated("apt-get update", PackageTimeoutMs);
            Util.RunElevated("apt-get install -y ksmbd-tools", PackageTimeoutMs);
            Console.WriteLine("ksmbd installation complete.");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error ensuring ksmbd is installed: {ex.Message}");
        }

        return false;
    }

    /// <summary>The SMB account the share is exposed under. Windows 11 blocks guest access
    /// outright, so a real (if entirely unsecret) account is the only way in.</summary>
    public const string KsmbdUser = "rt4k";
    private const string KsmbdPassword = "rt4k";

    /// <summary>
    /// Makes sure libfuse3 is present and reachable under the name FuseDotNet loads it by.
    /// A stock Raspberry Pi OS image has fuse3 but not always the shared library, and nothing
    /// else we install pulls it in, so a fresh Pi fails to mount without this.
    /// </summary>
    public bool EnsureFuseInstalled()
    {
        try
        {
            string? library = FindFuseLibrary();

            if (library == null)
            {
                Console.WriteLine("libfuse3 is not installed, installing it");

                Util.RunElevated("apt-get update", PackageTimeoutMs);

                // Only "fuse3" is asked for by name: the runtime library package is versioned
                // after the ABI (libfuse3-3 on bookworm, libfuse3-4 on trixie), so naming it
                // directly breaks on whichever release we didn't think of. fuse3 depends on the
                // right one for the release we're actually on.
                Util.RunElevated("apt-get install -y fuse3", PackageTimeoutMs);

                // Newly installed libraries aren't in the cache ldconfig -p reads from yet
                try { Util.RunElevated("ldconfig"); } catch { }

                library = FindFuseLibrary();
            }

            if (library == null)
            {
                Console.WriteLine("Could not find libfuse3 even after installing it");
                return false;
            }

            // FuseDotNet dlopens the unversioned "libfuse3.so", which only the -dev package
            // ships, so point a link next to the app at whatever real library we found
            Util.RunCommand("ln", $"-sf {library} libfuse3.so");
            Console.WriteLine($"Using libfuse3 at {library}");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error ensuring libfuse3 is installed: {ex.Message}");
        }

        return false;
    }

    /// <summary>Locates the versioned libfuse3 shared library, or null if it isn't installed.</summary>
    private static string? FindFuseLibrary()
    {
        List<string> candidates = [];

        try
        {
            // ldconfig knows where the package landed regardless of architecture, which beats
            // guessing at multiarch directory names
            foreach (string line in Util.RunCommand("ldconfig", "-p").Split('\n'))
            {
                int arrow = line.IndexOf("=> ", StringComparison.Ordinal);

                if (arrow > 0)
                {
                    candidates.Add(line[(arrow + 3)..].Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not query ldconfig for libfuse3: {ex.Message}");
        }

        // Also look in the usual locations, in case ldconfig isn't available or its cache is stale
        string[] directories =
        [
            "/usr/lib/aarch64-linux-gnu",
            "/usr/lib/arm-linux-gnueabihf",
            "/lib/aarch64-linux-gnu",
            "/usr/lib"
        ];

        foreach (string directory in directories.Where(Directory.Exists))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(directory, "libfuse3.so.*"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not search {directory} for libfuse3: {ex.Message}");
            }
        }

        // The ABI version in the file name tracks the Debian release (3 on bookworm, 4 on
        // trixie), so anything matching is accepted rather than a hard-coded list that would
        // need editing every time Debian moves on. FuseDotNet is built against the 3.x API and
        // later libfuse releases have kept it, so the one it was written against wins if it's
        // installed, and otherwise the oldest available ABI is the closest thing to it.
        return candidates
            .Where(File.Exists)
            .Select(path => (Path: path, Version: FuseAbiVersion(path)))
            .Where(candidate => candidate.Version != null)
            .OrderBy(candidate => candidate.Version == 3 ? 0 : 1)
            .ThenBy(candidate => candidate.Version)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns the ABI version from a "libfuse3.so.&lt;n&gt;" path, or null if it isn't one.
    /// </summary>
    private static int? FuseAbiVersion(string path)
    {
        const string prefix = "libfuse3.so.";
        string name = Path.GetFileName(path);

        // Only a bare major version counts: a fully qualified "libfuse3.so.3.10.5" is the same
        // library reached by a name nothing else links against, and matching it would mean
        // pinning ourselves to a file that a package update deletes out from under the symlink.
        return name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name[prefix.Length..], out int version)
            ? version
            : null;
    }

    public bool EnsureKsmbdConfig()
    {
        string configFilePath = "/etc/ksmbd/ksmbd.conf";

        // Define the new share configuration
        StringBuilder sb = new();
        sb.AppendLine("[global]");
        sb.AppendLine("   map to guest = never");
        sb.AppendLine("   browseable = yes");
        sb.AppendLine("   create mask = 0777");
        sb.AppendLine("   directory mask = 0777");
        sb.AppendLine("   writeable = yes");
        sb.AppendLine("   guest ok = no");
        sb.AppendLine("   netbios name = rt4k.local");
        sb.AppendLine("");
        sb.AppendLine("[sd]");
        sb.AppendLine($"   path = {Directory.GetCurrentDirectory()}/serialfs");
        sb.AppendLine($"   valid users = {KsmbdUser}");
        // The FUSE mount is owned by root and reports 0777, so the share runs as root rather
        // than as an account that has no business owning anything on this system
        sb.AppendLine("   force user = root");
        sb.AppendLine("   force group = root");


        try
        {
            string config = sb.ToString();

            // Only rewrite when it's actually different. This runs on every startup, and the
            // disable/adduser work below costs a couple of seconds of sudo calls each time.
            if (File.Exists(configFilePath) && File.ReadAllText(configFilePath) == config)
            {
                return true;
            }

            // Write the new configuration to the file
            Util.RunElevated("mkdir -p /etc/ksmbd");
            File.WriteAllText(configFilePath, config);
            Console.WriteLine("ksmbd configuration file replaced with new configuration.");

            // The share is only meaningful while the FUSE mount is live, so it's started and
            // stopped by FuseDaemon rather than by systemd at boot. Left enabled, ksmbd would
            // come up first and export the empty mount point directory.
            try { Util.RunElevated("systemctl disable ksmbd"); } catch { }

            return EnsureKsmbdUser();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Makes sure the SMB account exists in ksmbd's user database. ksmbd only accepts users that
    /// also exist on the system, so a locked-down system account is created for it first.
    /// </summary>
    private static bool EnsureKsmbdUser()
    {
        try
        {
            // Already there on every run but the first, and useradd fails rather than no-ops
            if (!File.ReadAllText("/etc/passwd").Split('\n').Any(line => line.StartsWith($"{KsmbdUser}:")))
            {
                Console.WriteLine($"Creating system account for the SMB user \"{KsmbdUser}\"");

                // No home directory, no shell, no password: this account exists purely so that
                // ksmbd has a uid to map the session onto
                Util.RunElevated($"useradd -M -N -s /usr/sbin/nologin {KsmbdUser}");
                Util.RunElevated($"passwd -l {KsmbdUser}");
            }

            // ksmbd.adduser refuses to overwrite an existing entry, so update it if it's there
            // and add it if it isn't. -p keeps it non-interactive. The database is a plain text
            // "<user>:<base64 password hash>" file, so it can just be read.
            string database = "/etc/ksmbd/ksmbdpwd.db";

            string command = File.Exists(database) &&
                File.ReadAllText(database).Split('\n').Any(line => line.StartsWith($"{KsmbdUser}:"))
                ? "-u" : "-a";

            Util.RunElevated($"ksmbd.adduser {command} {KsmbdUser} -p {KsmbdPassword}");

            Console.WriteLine($"ksmbd user \"{KsmbdUser}\" configured.");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to configure the ksmbd user: {ex.Message}");
        }

        return false;
    }
}