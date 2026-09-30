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

    private int updating;
    private volatile int updateProgress;
    private volatile string updateError = "";
    private volatile string updatePhase = "";
    private Task? updateTask;
    private static readonly HttpClient http = CreateHttpClient();
    private const long MaxUpdateSize = 256 * 1024 * 1024;
    private const int MaxReleaseNotesLength = 64 * 1024;
    private const string LatestReleaseUrl = "https://api.github.com/repos/Guspaz/rt4k_pi/releases/latest";
    private const string ExecutableAsset = "rt4k_pi";
    private const string HashAsset = "rt4k_pi.sha256";
    private const string RebootRequiredFile = "/var/run/reboot-required";

    // A full-upgrade on a Pi Zero 2 W can legitimately take a long time
    private const int SystemUpgradeTimeoutMs = 60 * 60 * 1000;

    private static readonly TimeSpan BackgroundCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackgroundCheckInterval = TimeSpan.FromHours(6);
    private CancellationTokenSource? backgroundCheckCts;
    private Task? backgroundCheckTask;

    private static ReleaseInfo? latestRelease;

    public bool Updating => Volatile.Read(ref updating) != 0;
    public int UpdateProgress => updateProgress;
    public string UpdateError => updateError;

    public static ReleaseInfo? LatestRelease => Volatile.Read(ref latestRelease);
    public static bool UpdateAvailable => LatestRelease is { } release && SemVer.Compare(release.Version, Program.VERSION) > 0;
    public static bool RebootRequired => OperatingSystem.IsLinux() && File.Exists(RebootRequiredFile);

    public sealed record ReleaseInfo(string Version, string Notes, string? PageUrl, Uri ExecutableUrl, Uri HashUrl);

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
        sb.AppendLine($"ExecStart={Path.Combine(AppContext.BaseDirectory, "rt4k_pi")}");
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
            string phase = updatePhase;
            if (phase == SystemUpgradePhase)
            {
                TimeSpan elapsed = systemUpgradeTimer.Elapsed;
                string time = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes} min" : "under a minute";
                return $"{phase}: {systemUpgradeDetail} — {time} so far";
            }
            return phase == DownloadingPhase ? $"{phase} ({UpdateProgress}%)" : phase;
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
        => CheckUpdateAsync().GetAwaiter().GetResult();

    public static async Task<string> CheckUpdateAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var info = await FetchLatestReleaseAsync(timeout.Token);
            Volatile.Write(ref latestRelease, info);
            return Program.Settings.LatestVersion = info.Version;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in CheckUpdate: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Checks for a new release shortly after startup and then periodically, so the Settings
    /// page can offer an update without anyone having to go looking for one.
    /// </summary>
    public void StartBackgroundChecks()
    {
        if (backgroundCheckTask != null) { return; }

        backgroundCheckCts = new CancellationTokenSource();
        CancellationToken token = backgroundCheckCts.Token;
        backgroundCheckTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BackgroundCheckDelay, token);
                while (!token.IsCancellationRequested)
                {
                    await CheckUpdateAsync();
                    await Task.Delay(BackgroundCheckInterval, token);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public async Task StopBackgroundChecksAsync()
    {
        backgroundCheckCts?.Cancel();
        if (backgroundCheckTask != null)
        {
            try { await backgroundCheckTask; } catch (OperationCanceledException) { }
        }
    }

    public void DoUpdate()
    {
        if (Interlocked.CompareExchange(ref updating, 1, 0) != 0)
        {
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            SetUpdateError("Unable to update on Windows");
            Volatile.Write(ref updating, 0);
            return;
        }

        updateProgress = 0;
        updateError = "";
        updatePhase = CheckingPhase;
        Console.WriteLine("Update triggered");
        updateTask = Task.Run(InstallUpdateAsync);
    }

    private const string CheckingPhase = "Checking for the latest release";
    private const string DownloadingPhase = "Downloading";
    private const string SystemUpgradePhase = "Updating the Pi's system software";
    private const string InstallingPhase = "Installing";
    private const string RestartingPhase = "Restarting";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API refuses requests without a User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"rt4k_pi/{SanitizeUserAgentVersion(Program.VERSION)}");
        return client;
    }

    private static string SanitizeUserAgentVersion(string version)
        => new(version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());

    private static async Task<ReleaseInfo> FetchLatestReleaseAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(token);
        var release = await System.Text.Json.JsonSerializer.DeserializeAsync(stream, UpdateJsonContext.Default.GitHubRelease, token)
            ?? throw new InvalidDataException("Empty release metadata.");

        if (release.Draft || release.Prerelease)
        {
            throw new InvalidDataException("Latest release is not a stable release.");
        }

        string version = (release.TagName ?? "").TrimStart('v', 'V');
        if (!SemVer.TryParse(version, out _, out string[] prerelease) || prerelease.Length != 0)
        {
            throw new InvalidDataException($"Invalid release tag '{release.TagName}'.");
        }

        Uri executableUrl = FindAsset(release, ExecutableAsset);
        Uri hashUrl = FindAsset(release, HashAsset);

        string notes = (release.Body ?? "").Replace("\r\n", "\n").Trim();
        if (notes.Length > MaxReleaseNotesLength)
        {
            notes = notes[..MaxReleaseNotesLength] + "\n\u2026";
        }

        string? pageUrl = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? page) && page.Scheme == Uri.UriSchemeHttps
            ? page.AbsoluteUri
            : null;

        return new ReleaseInfo(version, notes, pageUrl, executableUrl, hashUrl);
    }

    private static Uri FindAsset(GitHubRelease release, string name)
    {
        string? url = release.Assets?.FirstOrDefault(a => a.Name == name)?.BrowserDownloadUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"Release is missing {name}.");
        }
        return uri;
    }

    private static async Task<byte[]> DownloadExpectedHashAsync(Uri url, CancellationToken token)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 4096) { throw new InvalidDataException("Invalid SHA-256 file."); }

        // sha256sum format: "<hex>  rt4k_pi"
        string text = await response.Content.ReadAsStringAsync(token);
        string hex = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        byte[] hash;
        try { hash = Convert.FromHexString(hex); }
        catch (FormatException) { throw new InvalidDataException("Invalid SHA-256 file."); }
        if (hash.Length != 32) { throw new InvalidDataException("Invalid SHA-256 file."); }
        return hash;
    }

    private async Task DownloadUpdateAsync(string archive, Uri url, byte[] expectedHash, CancellationToken token)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;
        if (length is <= 0 or > MaxUpdateSize) { throw new InvalidDataException("Invalid update size."); }

        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > MaxUpdateSize) { throw new InvalidDataException("Update exceeds 256 MiB."); }
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            sha256.AppendData(buffer, 0, read);
            if (length.HasValue) { updateProgress = (int)Math.Min(90, total * 90 / length.Value); }
        }

        if (total == 0 || (length.HasValue && total != length.Value) ||
            !CryptographicOperations.FixedTimeEquals(sha256.GetHashAndReset(), expectedHash))
        {
            throw new InvalidDataException("Update size or SHA-256 mismatch.");
        }

        await output.FlushAsync(token);
        output.Flush(flushToDisk: true);
    }

    private async Task InstallUpdateAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".update-" + Guid.NewGuid().ToString("N"));
        string executable = Path.Combine(AppContext.BaseDirectory, "rt4k_pi");
        string backup = executable + ".previous";
        bool replaced = false;
        try
        {
            Directory.CreateDirectory(directory);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var info = await FetchLatestReleaseAsync(timeout.Token);
            Volatile.Write(ref latestRelease, info);
            Program.Settings.LatestVersion = info.Version;
            if (SemVer.Compare(info.Version, Program.VERSION) <= 0)
            {
                throw new InvalidOperationException("Already up to date.");
            }

            updatePhase = DownloadingPhase;
            byte[] expectedHash = await DownloadExpectedHashAsync(info.HashUrl, timeout.Token);
            string staged = Path.Combine(directory, ExecutableAsset);
            await DownloadUpdateAsync(staged, info.ExecutableUrl, expectedHash, timeout.Token);

            var file = new FileInfo(staged);
            if (!file.Exists || file.LinkTarget != null || file.Length < 64 || file.Length > MaxUpdateSize)
            {
                throw new InvalidDataException("Download is not a regular rt4k_pi executable.");
            }

            using (var stream = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                byte[] header = new byte[20];
                stream.ReadExactly(header);
                if (!header.AsSpan(0, 6).SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 }) ||
                    header[18] != 183 || header[19] != 0)
                {
                    throw new InvalidDataException("Update is not a Linux ARM64 executable.");
                }
                stream.Flush(flushToDisk: true);
            }

            // System packages first: if they fail, the running version is left untouched
            updatePhase = SystemUpgradePhase;
            UpgradeSystemPackages();

            updatePhase = InstallingPhase;
            File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Copy(executable, backup, overwrite: true);
            File.Move(staged, executable, overwrite: true);
            replaced = true;
            try { Directory.Delete(directory, recursive: true); } catch { }
            updateProgress = 100;
            updatePhase = RestartingPhase;
            Console.WriteLine($"Installed rt4k_pi {info.Version}, restarting");

            // Past this point the new version is in place and must never be rolled back: the
            // restart stops our whole cgroup, which can kill systemctl itself mid-command and make
            // it look like a failure even though systemd already has the restart queued.
            replaced = false;
            try { DoInstall(); }
            catch (Exception ex) { Console.WriteLine($"Restart after update reported: {ex.Message}"); }
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update error: {ex.Message}");
            if (replaced)
            {
                try { File.Move(backup, executable, overwrite: true); }
                catch (Exception rollback) { Console.WriteLine($"Update rollback failed: {rollback.Message}"); }
            }
            SetUpdateError($"Update error: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
            catch (Exception ex) { Console.WriteLine($"Update cleanup failed: {ex.Message}"); }
            Volatile.Write(ref updating, 0);
        }
    }

    private void SetUpdateError(string error)
    {
        updateError = error;
        updateProgress = 0;
        updatePhase = "";
    }

    private volatile string systemUpgradeDetail = "";
    private readonly System.Diagnostics.Stopwatch systemUpgradeTimer = new();

    /// <summary>
    /// Brings the Pi's packages up to date, so an existing install ends up on the same footing
    /// as a fresh one. Never prompts: config files the user changed are kept as they are.
    /// Progress is one percentage across the whole step so it never goes backwards: preparing
    /// 0-10%, downloading 10-40%, installing 40-100%.
    /// </summary>
    private void UpgradeSystemPackages()
    {
        const string env = "env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a";
        const string options = "-o DPkg::Lock::Timeout=300 -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold";
        systemUpgradeTimer.Restart();
        systemUpgradePercent = 0;
        try
        {
            Console.WriteLine("Updating system packages");
            systemUpgradeDetail = "checking for updates (0%)";
            Util.RunElevated($"{env} dpkg --configure -a", SystemUpgradeTimeoutMs);
            Util.RunElevated($"{env} apt-get {options} update", PackageTimeoutMs);

            string plan = Util.RunElevated($"{env} apt-get {options} -s full-upgrade", PackageTimeoutMs);
            int packages = plan.Split('\n').Count(line => line.StartsWith("Inst ", StringComparison.Ordinal));
            if (packages == 0)
            {
                systemUpgradeDetail = "already up to date (100%)";
                Console.WriteLine("System packages already up to date");
                return;
            }

            Console.WriteLine($"Upgrading {packages} system package(s)");
            systemUpgradeDetail = $"{packages} package{(packages == 1 ? "" : "s")} to update (10%)";
            Util.RunElevatedStreaming($"{env} apt-get {options} -o APT::Status-Fd=1 -y full-upgrade", SystemUpgradeTimeoutMs, ReportAptStatus);
            systemUpgradeDetail = "finished (100%)";
            Console.WriteLine(RebootRequired ? "System packages updated; a reboot is required" : "System packages updated");
        }
        catch (Exception ex) when (ex.Message.Contains("get lock", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("frontend lock", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Pi is busy installing other system updates. Try again in a few minutes.", ex);
        }
        finally
        {
            systemUpgradeTimer.Stop();
        }
    }

    private int systemUpgradePercent;

    /// <summary>
    /// Parses apt's machine-readable status lines ("dlstatus:3:42.5:Retrieving file 3 of 12",
    /// "pmstatus:libc6:67.1:Unpacking libc6") into the status shown on the Settings page.
    /// </summary>
    private void ReportAptStatus(string line)
    {
        string[] fields = line.Split(':', 4);
        if (fields.Length < 4 ||
            !double.TryParse(fields[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent))
        {
            return;
        }

        percent = Math.Clamp(percent, 0, 100);
        (int overall, string detail) = fields[0] switch
        {
            "dlstatus" => ((int)(10 + percent * 0.3), $"downloading ({fields[3].Trim()})"),
            "pmstatus" => ((int)(40 + percent * 0.6), $"installing {fields[1]}"),
            _ => (-1, "")
        };
        if (overall < 0) { return; }

        overall = Math.Max(overall, systemUpgradePercent);
        systemUpgradePercent = overall;
        systemUpgradeDetail = $"{detail} ({overall}%)";
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