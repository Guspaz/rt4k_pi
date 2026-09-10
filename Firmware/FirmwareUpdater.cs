namespace rt4k_pi;

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

public interface IFirmwareDevice
{
    bool IsConnected { get; }
    long ConnectionRevision { get; }
    Task ExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken token);
    Task<List<string>> CommandAsync(string command, Func<string, bool> terminal, int timeoutMs, CancellationToken token);
    Task UploadAsync(string path, Stream data, long length, string sha256, Action<long> progress, CancellationToken token);
}

public sealed class FirmwareUpdater
{
    // Bump this when rt4k_pi depends on newer firmware behavior (including the default baud rate).
    public static readonly Version MinimumSupportedVersion = new(1, 75, 0);

    public static bool IsSupportedVersion(string? version)
    {
        if (!Version.TryParse(version, out var parsed)) { return false; }
        if (parsed.Build < 0) { parsed = new Version(parsed.Major, parsed.Minor, 0); }
        return parsed >= MinimumSupportedVersion;
    }

    private const string Binary = "rt4kup.bin";
    private readonly IFirmwareDevice device;
    private readonly FirmwareCatalog catalog;
    private readonly string directory;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private FirmwareJournal? journal;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private Task? cleanupMonitor;
    private string? lastCleanupError;
    private bool active;
    private bool stopping;
    private readonly bool corrupt;
    private long bytes;
    private long? total;
    private long connection;
    private string? currentVersion;
    private string? currentModel;

    public FirmwareUpdater(IFirmwareDevice device, FirmwareCatalog catalog, string directory, TimeProvider? time = null)
    {
        this.device = device;
        this.catalog = catalog;
        this.directory = directory;
        this.time = time ?? TimeProvider.System;
        string path = Path.Combine(directory, "journal.json");
        try
        {
            Directory.CreateDirectory(directory);
            DeleteDownload();
            if (!File.Exists(path) && File.Exists(Path.Combine(directory, "journal.new"))) { path = Path.Combine(directory, "journal.new"); }
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 32768) { throw new InvalidDataException("Oversized journal."); }
                journal = JsonSerializer.Deserialize(File.ReadAllText(path), FirmwareJsonContext.Default.FirmwareJournal) ?? throw new InvalidDataException("Empty journal.");
                ValidateJournal(journal);
                if (journal.NeedsRecovery || journal.Phase is not ("Completed" or "Canceled" or "Failed"))
                {
                    bool touched = journal.PublicationStarted || journal.FlashStarted;
                    journal = journal with
                    {
                        Phase = touched ? "WaitingForDevice" : "Canceled",
                        NeedsRecovery = touched,
                        Message = touched
                            ? "The update was interrupted. Keep the original RT4K powered on while its update status is checked. The update will not resume."
                            : "The previous update stopped before installation. Temporary downloads were removed; completed files on the RT4K were kept. You can start a new update."
                    };
                    if (path.EndsWith("journal.new", StringComparison.Ordinal)) { FirmwarePersistence.Save(directory, journal); }
                }
            }
            File.Delete(Path.Combine(directory, "journal.new"));
        }
        catch (Exception ex)
        {
            corrupt = true;
            journal = new() { Phase = "CleanupRequired", NeedsRecovery = true, Message = "The previous update's records could not be read, so its files cannot be safely cleaned up. No update will start. " + ex.Message };
        }
    }

    public FirmwareProgress Status
    {
        get
        {
            lock (gate)
            {
                return new(journal?.Phase ?? "Idle", journal?.Message ?? "Ready.", journal?.Version, active, active && journal?.FlashStarted != true && !stopping && cancellation?.IsCancellationRequested == false && journal?.Phase != "Cleaning", journal?.NeedsRecovery ?? false, bytes, total);
            }
        }
    }

    public (string? Version, string? Model) Current { get { lock (gate) { return (currentVersion, currentModel); } } }

    public void Start(FirmwareRelease release)
    {
        if (!IsSupportedVersion(release.Version))
        {
            throw new InvalidOperationException($"Firmware {release.Version} is not supported. rt4k_pi requires firmware {MinimumSupportedVersion} or newer for compatible serial support and default baud rate.");
        }
        lock (gate)
        {
            if (active || stopping || journal?.NeedsRecovery == true) { throw new InvalidOperationException("An update is running or temporary files still need to be cleaned up."); }
            journal = new() { Version = release.Version };
            try { FirmwarePersistence.Save(directory, journal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                journal = journal with { Phase = "Failed", Message = "Could not start the update. No firmware was sent. " + ex.Message };
                bytes = 0;
                total = null;
                throw new IOException(journal.Message, ex);
            }
            cancellation = new();
            active = true;
            currentVersion = null;
            currentModel = null;
            bytes = 0;
            total = null;
            worker = Task.Run(() => RunAsync(release, cancellation.Token));
        }
    }

    public bool Cancel()
    {
        lock (gate)
        {
            if (!Status.CanCancel) { return false; }
            cancellation!.Cancel();
            journal = journal! with { Message = "Canceling the update and removing temporary files. Please wait." };
            return true;
        }
    }

    public void StartCleanupMonitor()
    {
        lock (gate)
        {
            if (!stopping) { cleanupMonitor ??= Task.Run(MonitorCleanupAsync); }
        }
    }

    private async Task MonitorCleanupAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try { await CleanupInterruptedAsync(shutdown.Token); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.WriteLine("Firmware cleanup: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), time, shutdown.Token); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
        }
    }

    public Task CleanupInterruptedAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            if (active || stopping || corrupt || journal?.NeedsRecovery != true) { return Task.CompletedTask; }
            if (journal.PublicationStarted && !journal.FlashStarted)
            {
                journal = journal with { Phase = "CleanupRequired", Message = "An older updater interrupted a file replacement. Check the RT4K SD card using the official instructions before starting another update. Existing files and backups have been preserved." };
                return Task.CompletedTask;
            }
            if (journal.FlashStarted && !device.IsConnected)
            {
                journal = journal with { Phase = "WaitingForDevice", Message = "Reconnect the original RT4K and leave it powered on so its firmware version can be checked. The update will not resume." };
                return Task.CompletedTask;
            }
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            active = true;
            bytes = 0;
            total = null;
            journal = journal with { Phase = "Cleaning", Message = "Checking the interrupted update. Please wait." };
            return worker = Task.Run(() => CleanInterruptedCoreAsync(cancellation.Token), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        Task? pending;
        Task? monitor;
        lock (gate)
        {
            stopping = true;
            shutdown.Cancel();
            cancellation?.Cancel();
            pending = worker;
            monitor = cleanupMonitor;
        }
        await Task.WhenAll(pending ?? Task.CompletedTask, monitor ?? Task.CompletedTask);
    }

    private void Save(FirmwareJournal next)
    {
        lock (gate)
        {
            // Publish memory first: even a failed durable write must not reopen cancellation
            // or authorize a second flash in this process.
            journal = next;
            FirmwarePersistence.Save(directory, next);
            bytes = 0;
            total = null;
        }
    }

    private void Phase(string phase, string message) => Save(journal! with { Phase = phase, Message = message });
    private void Progress(long done, long? length) { lock (gate) { bytes = done; total = length; } }
    private void DeleteDownload() => File.Delete(Path.Combine(directory, "download.zip"));

    private async Task RunAsync(FirmwareRelease release, CancellationToken token)
    {
        try
        {
            await device.ExclusiveAsync(async sessionToken =>
            {
                try
                {
                    var initial = await ReadDeviceAsync(sessionToken);
                    connection = device.ConnectionRevision;
                    Save(journal! with { Model = initial.Model, OriginalVersion = initial.Version });
                    Phase("Downloading", "Downloading and verifying the official ZIP on the Pi.");
                    DeleteDownload();
                    using (var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken))
                    {
                        downloadTimeout.CancelAfter(TimeSpan.FromMinutes(15));
                        await catalog.DownloadAsync(release, Path.Combine(directory, "download.zip"), Progress, downloadTimeout.Token);
                    }
                    Phase("Validating", "Validating the archive and selecting only this model's firmware files.");
                    using var zip = ZipFile.OpenRead(Path.Combine(directory, "download.zip"));
                    FirmwareImage[] images = await FirmwareCatalog.SelectImagesAsync(zip, initial.Model, release.Version, sessionToken);
                    FirmwareImage rbf = images[0], bin = images[1];
                    CheckConnection();
                    await RequireIdentityAsync(sessionToken);
                    var space = Fields((await QueryAsync("df", sessionToken)).FirstOrDefault(l => l.StartsWith("df total=")) ?? "");
                    long required = rbf.Entry.Length + bin.Entry.Length + 1024 * 1024;
                    if (!long.TryParse(space.GetValueOrDefault("free"), out long free) || free * 1024 < required) { throw new IOException("The RT4K SD card does not report enough free space to safely stage this update."); }
                    Save(journal! with { Rbf = rbf.Name, DeviceTouched = true });
                    await UploadAsync(rbf, rbf.Name, sessionToken);
                    await UploadAsync(bin, Binary, sessionToken);
                    await RequireIdentityAsync(sessionToken);
                    sessionToken.ThrowIfCancellationRequested();
                    Phase("Checking", "Asking the RT4K to approve installation.");
                    var check = await QueryAsync("fwup check", sessionToken);
                    var fields = Fields(check.SingleOrDefault(l => l.StartsWith("fwup ok ")) ?? "");
                    string? flashToken = fields.GetValueOrDefault("token");
                    if (fields.GetValueOrDefault("version") != release.Version || flashToken == null || !Regex.IsMatch(flashToken, @"\A[0-9a-fA-F]{1,8}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) { throw new InvalidDataException("The RT4K rejected the update binary or returned an unexpected version/token. " + string.Join("; ", check)); }
                    CheckConnection();
                    lock (gate)
                    {
                        sessionToken.ThrowIfCancellationRequested();
                        Save(journal! with { FlashStarted = true, NeedsRecovery = true, Phase = "Flashing", Message = "Flashing may now be in progress. Do not turn off or unplug the RT4K. Cancellation is no longer safe." });
                    }
                    // Never retry go: a missing reply can mean the bootloader already took over.
                    try
                    {
                        CheckConnection();
                        await QueryAsync("fwup go " + flashToken, sessionToken);
                    }
                    catch (Exception ex) { Console.WriteLine("Firmware flash command outcome uncertain: " + ex.Message); }
                    await VerifyBootAsync(initial.Uptime, sessionToken);
                    Save(journal! with { Phase = "Completed", NeedsRecovery = false, Message = "The RT4K reports firmware " + release.Version + ". Update complete." });
                }
                catch (Exception ex)
                {
                    await HandleFailureAsync(ex);
                }
            }, token);
        }
        catch (Exception ex) { RecordFailure(ex.Message); }
        finally { FinishWorker(); }
    }

    private async Task UploadAsync(FirmwareImage image, string destination, CancellationToken token)
    {
        CheckConnection();
        Phase("Transferring", "Sending " + image.Name + " to the RT4K");
        using var input = image.Entry.Open();
        await device.UploadAsync(destination, input, image.Entry.Length, image.Sha256, done => Progress(done, image.Entry.Length), token);
        token.ThrowIfCancellationRequested();
        CheckConnection();
    }

    private Task HandleFailureAsync(Exception error)
    {
        Console.WriteLine("Firmware update: " + error.Message);
        if (journal!.FlashStarted)
        {
            RecordFailure("The RT4K has not confirmed the update yet. Keep it powered on while we wait for its new version. " + error.Message);
            return Task.CompletedTask;
        }
        try
        {
            bool canceled = cancellation?.IsCancellationRequested == true;
            Save(journal with { Phase = canceled ? "Canceled" : "Failed", NeedsRecovery = false, Message = (canceled ? "Canceled before installation. " : "Update stopped before installation: " + error.Message + " ") + "Installed firmware is unchanged. Completed files on the RT4K were kept." });
        }
        catch (Exception saveError) { RecordFailure(error.Message + " Could not save update status: " + saveError.Message, true); }
        return Task.CompletedTask;
    }

    private void RecordFailure(string message, bool requireCleanup = false)
    {
        bool cleanup = requireCleanup || journal!.FlashStarted;
        try { Save(journal! with { Phase = cleanup ? "CleanupRequired" : "Failed", NeedsRecovery = cleanup, Message = message }); }
        catch (Exception ex)
        {
            lock (gate) { journal = journal! with { Phase = "CleanupRequired", NeedsRecovery = true, Message = message + " Could not save cleanup records: " + ex.Message }; }
        }
    }

    private void FinishWorker()
    {
        try { DeleteDownload(); }
        catch (Exception ex) { RecordFailure("Could not remove the temporary download: " + ex.Message, true); }
        lock (gate)
        {
            active = false;
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    private async Task CleanInterruptedCoreAsync(CancellationToken token)
    {
        try
        {
            if (journal!.FlashStarted)
            {
                await device.ExclusiveAsync(async sessionToken =>
                {
                    var state = await ReadDeviceAsync(sessionToken);
                    if (state.Model != journal.Model) { throw new InvalidOperationException("Connect the original RT4K so its firmware version can be checked."); }
                    connection = device.ConnectionRevision;
                    if (state.Version != journal.Version) { throw new InvalidOperationException("The RT4K has not confirmed the new firmware. Keep it powered on."); }
                }, token);
            }
            DeleteDownload();
            Save(journal! with { Phase = journal!.FlashStarted ? "Completed" : "Canceled", NeedsRecovery = false, Message = journal.FlashStarted ? "The RT4K is running the selected firmware." : "The interrupted update was canceled. Temporary downloads were removed; completed files on the RT4K were kept. You can start a new update." });
            lastCleanupError = null;
        }
        catch (Exception ex)
        {
            string message = "Waiting to confirm update status. " + ex.Message;
            if (message != lastCleanupError)
            {
                lastCleanupError = message;
                RecordFailure(message, true);
            }
            else
            {
                // Repeated checks while a device is unavailable must not keep rewriting the SD card.
                lock (gate) { journal = journal! with { Phase = "CleanupRequired", NeedsRecovery = true, Message = message }; }
            }
        }
        finally { FinishWorker(); }
    }

    private async Task VerifyBootAsync(long? previousUptime, CancellationToken token)
    {
        Phase("Rebooting", "Waiting for the RT4K to reboot and report the selected firmware. Keep RT4K power connected.");
        long started = time.GetTimestamp();
        bool offline = false;
        while (time.GetElapsedTime(started) < TimeSpan.FromMinutes(3))
        {
            await Task.Delay(TimeSpan.FromSeconds(3), time, token);
            (int Model, string Version, long? Uptime) state;
            try { state = await ReadDeviceAsync(token); }
            catch (Exception) when (!token.IsCancellationRequested) { offline = true; continue; }
            TimeSpan elapsed = time.GetElapsedTime(started);
            bool rebooted = offline || state.Version != journal!.OriginalVersion || (previousUptime.HasValue && state.Uptime.HasValue && state.Uptime.Value < previousUptime.Value + elapsed.TotalSeconds - 2);
            if (state.Model == journal!.Model && state.Version == journal.Version && rebooted)
            {
                connection = device.ConnectionRevision;
                return;
            }
        }
        throw new TimeoutException("The selected firmware was not confirmed after three minutes. This is not proof that flashing failed; no automatic retry will be attempted.");
    }

    private void CheckConnection()
    {
        if (!device.IsConnected || device.ConnectionRevision != connection)
        {
            throw new IOException("The connection changed. Reconnect the original RT4K for automatic cleanup. The update will not resume.");
        }
    }

    private async Task RequireIdentityAsync(CancellationToken token)
    {
        CheckConnection();
        var state = await ReadDeviceAsync(token);
        if (state.Model != journal!.Model || state.Version != journal.OriginalVersion) { throw new InvalidOperationException("Connected device model or firmware changed during the update."); }
        CheckConnection();
    }

    private async Task<(int Model, string Version, long? Uptime)> ReadDeviceAsync(CancellationToken token)
    {
        var fields = new Dictionary<string, string>();
        foreach (string line in await QueryAsync("status", token))
        {
            if (line.StartsWith("status ")) { foreach (var pair in Fields(line)) { fields[pair.Key] = pair.Value; } }
        }
        if (!int.TryParse(fields.GetValueOrDefault("model"), out int model) || model is not (0 or 1) || !Version.TryParse(fields.GetValueOrDefault("fw"), out var version) || fields.GetValueOrDefault("sd") != "1") { throw new InvalidOperationException("An awake RT4K Pro or CE with an SD card and a working status command is required."); }
        string firmware = version.ToString();
        lock (gate) { currentVersion = firmware; currentModel = model == 0 ? "RT4K Pro" : "RT4K CE"; }
        return (model, firmware, long.TryParse(fields.GetValueOrDefault("uptime_s"), out long uptime) ? uptime : null);
    }

    private Task<List<string>> QueryAsync(string command, CancellationToken token, int timeoutMs = 5000)
    {
        string verb = command.Split(' ')[0];
        return device.CommandAsync(command, line => verb == "status" ? line.StartsWith("status oerr=") : line.StartsWith(verb + " ") || line.StartsWith(verb + ":"), timeoutMs, token);
    }

    private static Dictionary<string, string> Fields(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Contains('=')).Select(p => p.Split('=', 2)).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1]);

    private static void ValidateJournal(FirmwareJournal state)
    {
        if (!state.DeviceTouched && !state.FlashStarted && !state.PublicationStarted) { return; }
        string prefix = state.Model switch { 0 => "rt4k_", 1 => "rt4kce_", _ => throw new InvalidDataException("Invalid model in firmware journal.") };
        if (!Version.TryParse(state.Version, out _) || !Version.TryParse(state.OriginalVersion, out _) || !Regex.IsMatch(state.Rbf, @"\A" + prefix + @"[a-z0-9_.-]+\.rbf\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new InvalidDataException("Invalid paths or hashes in firmware journal; automatic cleanup is unsafe.");
        }
    }
}
