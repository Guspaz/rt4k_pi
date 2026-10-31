namespace rt4k_pi;

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using rt4k_pi.Filesystem;

public enum FactoryResetFirmware { LatestExperimental, LatestStable, FromImage }

public record FactoryResetStatus(string Phase, string Message, string? CurrentItem, int Percent, SdSyncSummary? Summary, bool Active, bool CanCancel, bool Failed);
public record FactoryResetReleases(string? SdVersion, string? SdDate, string? Stable, string? Experimental, string? Error,
    bool SdSupported = false, bool StableSupported = false, bool ExperimentalSupported = false);

public record FactoryResetRecord
{
    public DateTimeOffset Started { get; init; }
    public bool PointOfNoReturn { get; init; }
    public bool Completed { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(FactoryResetRecord))]
[JsonSerializable(typeof(FactoryResetStatus))]
[JsonSerializable(typeof(FactoryResetReleases))]
public partial class FactoryResetJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Makes the RT4K's SD card match the latest official SD card image, then installs firmware.
/// Pi work (download, hashing) runs alongside the RT4K's work (scan, device hashing). It can be
/// canceled until the first change to the card. Interrupted resets are reported, never resumed.
/// </summary>
public sealed class FactoryReset
{
    private const string DownloadDirectory = "/var/tmp/rt4k-sdreset";

    // Overall progress, by phase
    private const int PrepareEnd = 10, CheckEnd = 35, ApplyEnd = 85;

    private readonly Serial serial;
    private readonly SdCardCatalog sdCatalog;
    private readonly FirmwareCatalog firmwareCatalog;
    private readonly FirmwareUpdater firmware;
    private readonly string recordPath;
    private readonly string downloadDirectory;
    private readonly Lock gate = new();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private FactoryResetStatus status = new("Idle", "Ready.", null, 0, null, false, false, false);
    private bool pastPointOfNoReturn;

    public FactoryReset(Serial serial, SdCardCatalog sdCatalog, FirmwareCatalog firmwareCatalog, FirmwareUpdater firmware, string directory)
    {
        this.serial = serial;
        this.sdCatalog = sdCatalog;
        this.firmwareCatalog = firmwareCatalog;
        this.firmware = firmware;
        Directory.CreateDirectory(directory);
        recordPath = Path.Combine(directory, "factory-reset.json");
        downloadDirectory = OperatingSystem.IsLinux() ? DownloadDirectory : Path.Combine(Path.GetTempPath(), "rt4k-sdreset");
        DeleteDownload();
        try
        {
            if (File.Exists(recordPath) && new FileInfo(recordPath).Length < 4096 &&
                JsonSerializer.Deserialize(File.ReadAllText(recordPath), FactoryResetJsonContext.Default.FactoryResetRecord) is { Completed: false })
            {
                status = new("Interrupted", "The last factory reset didn't finish. Run it again.", null, 0, null, false, false, true);
            }
        }
        catch (Exception ex) { Console.WriteLine("Factory reset record: " + ex.Message); }
    }

    public FactoryResetStatus Status { get { lock (gate) { return status; } } }
    public bool Active { get { lock (gate) { return status.Active; } } }

    public async Task<FactoryResetReleases> GetReleasesAsync(CancellationToken token)
    {
        try
        {
            var sd = (await sdCatalog.GetAsync(token))[0];
            var releases = await firmwareCatalog.GetAsync(token, forceRefresh: true);
            string? stable = releases.FirstOrDefault(r => !r.Experimental)?.Version;
            string? experimental = releases.FirstOrDefault()?.Version;
            return new(sd.Version, sd.Date, stable, experimental, null,
                FirmwareUpdater.IsSupportedVersion(sd.Version), FirmwareUpdater.IsSupportedVersion(stable), FirmwareUpdater.IsSupportedVersion(experimental));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            return new(null, null, null, null, "Could not load the official releases: " + ex.Message);
        }
    }

    /// <summary>Starts a reset. The caller must hold the shared update start lock.</summary>
    public void Start(FactoryResetFirmware choice, bool fastChecks)
    {
        lock (gate)
        {
            if (status.Active) { throw new InvalidOperationException("A factory reset is already running."); }
            if (firmware.Status is { Active: true } or { NeedsRecovery: true }) { throw new InvalidOperationException("Wait for the firmware update to finish."); }
            if (Program.Installer.Updating) { throw new InvalidOperationException("Wait for the rt4k_pi application update to finish."); }
            if (!serial.IsConnected) { throw new InvalidOperationException("Connect the RT4K first."); }
            cancellation = new();
            pastPointOfNoReturn = false;
            status = new("Preparing", "Getting ready.", null, 0, null, true, true, false);
            worker = Task.Run(() => RunAsync(choice, fastChecks, cancellation.Token));
        }
    }

    public bool Cancel()
    {
        lock (gate)
        {
            if (!status.Active || !status.CanCancel || cancellation == null) { return false; }
            cancellation.Cancel();
            status = status with { Message = "Stopping after the current file…", CanCancel = false };
            return true;
        }
    }

    private void Update(string? phase = null, string? message = null, string? item = null, double? percent = null, SdSyncSummary? summary = null)
    {
        lock (gate)
        {
            int next = percent.HasValue ? Math.Clamp((int)percent.Value, 0, 100) : status.Percent;
            status = status with
            {
                Phase = phase ?? status.Phase,
                Message = message ?? status.Message,
                CurrentItem = item ?? (phase != null ? null : status.CurrentItem),
                Percent = Math.Max(status.Percent, next),
                Summary = summary ?? status.Summary,
                CanCancel = !pastPointOfNoReturn && cancellation?.IsCancellationRequested == false
            };
        }
    }

    private static double Scale(int start, int end, long done, long total) => start + (end - start) * (total <= 0 ? 1.0 : Math.Min(1.0, (double)done / total));

    private async Task RunAsync(FactoryResetFirmware choice, bool fastChecks, CancellationToken token)
    {
        bool failed = false;
        string message = "";
        Program.FuseDaemon?.PauseShare();
        try
        {
            SaveRecord(new() { Started = DateTimeOffset.UtcNow });
            Update("Preparing", "Checking the official releases.");
            var sd = (await sdCatalog.GetAsync(token))[0];
            FirmwareRelease? release = null;
            if (choice == FactoryResetFirmware.FromImage && !FirmwareUpdater.IsSupportedVersion(sd.Version))
            {
                throw new InvalidDataException($"The firmware included with the SD card files ({sd.Version}) is too old for rt4k_pi.");
            }
            if (choice != FactoryResetFirmware.FromImage)
            {
                var releases = await firmwareCatalog.GetAsync(token, forceRefresh: true);
                release = releases.FirstOrDefault(r => choice == FactoryResetFirmware.LatestExperimental || !r.Experimental);
                if (release == null || !FirmwareUpdater.IsSupportedVersion(release.Version))
                {
                    throw new InvalidDataException($"The latest {(choice == FactoryResetFirmware.LatestStable ? "stable" : "experimental")} firmware ({release?.Version ?? "none"}) is too old for rt4k_pi.");
                }
            }

            string zipPath = Path.Combine(downloadDirectory, "sdcard.zip");
            DeleteDownload();
            Directory.CreateDirectory(downloadDirectory);
            var fs = new SerialFs(serial);
            SdSyncPlan? plan = null;
            SdImage? image = null;
            SdCardSync.PiHasher? hasher = null;
            Task hashing = Task.CompletedTask;
            using var piCancel = CancellationTokenSource.CreateLinkedTokenSource(token);

            await serial.RunExclusiveAsync(async session =>
            {
                Update("Preparing", "Checking the RT4K.");
                int model = await RequireDeviceAsync(session);

                Update("Reading", "Downloading the SD card files and reading the RT4K's SD card.");
                long downloaded = 0, downloadTotal = 0;
                Task download = Task.Run(async () =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(piCancel.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(20));
                    await sdCatalog.DownloadAsync(sd, zipPath, (done, total) =>
                    {
                        downloaded = done;
                        downloadTotal = total ?? 0;
                        Update(percent: Scale(0, PrepareEnd, done, total ?? done * 2));
                    }, timeout.Token);
                }, piCancel.Token);

                SdDeviceTree tree;
                try
                {
                    tree = await Task.Run(() => SdCardSync.Scan(fs, item => Update(item: item), session), session);
                    await download;
                }
                catch
                {
                    piCancel.Cancel();
                    try { await download; } catch { }
                    throw;
                }

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    image = SdCardSync.LoadImage(zip);
                    if (choice == FactoryResetFirmware.FromImage)
                    {
                        // Fail before anything changes if the image's firmware doesn't suit this model.
                        await FirmwareCatalog.SelectImagesAsync(zip, model, sd.Version, session, sdCardImage: true);
                    }
                }

                hasher = new SdCardSync.PiHasher(zipPath, image);
                // Firmware 1.80.0+ takes uploads without a hash, so there is nothing for the Pi to compute
                if (!serial.SupportsUnhashedTransfers) { hashing = hasher.RunAsync(piCancel.Token); }
                Update("Checking", "Comparing the files on the RT4K with the official SD card.");
                var reporter = new SdSyncReporter
                {
                    Item = item => Update(item: item),
                    Hashing = (done, total) => Update(percent: Scale(PrepareEnd, CheckEnd, done, total))
                };
                plan = await Task.Run(() => SdCardSync.PlanAsync(fs, tree, image, hasher, reporter, fastChecks, session), session);
                var s = plan.Summary;
                Update(percent: CheckEnd, summary: s,
                    message: $"Unchanged {s.Unchanged}, replace {s.Replace}, add {s.Add}, delete {s.Delete}, rename {s.Rename}.");

                lock (gate)
                {
                    session.ThrowIfCancellationRequested();
                    pastPointOfNoReturn = true;
                    status = status with { CanCancel = false };
                }
                SaveRecord(new() { Started = DateTimeOffset.UtcNow, PointOfNoReturn = true });

                Update("Writing", "Updating the RT4K's SD card. Do not unplug either device.");
                var applying = new SdSyncReporter
                {
                    Item = item => Update(item: item),
                    Applying = (done, total) => Update(percent: Scale(CheckEnd, ApplyEnd, done, total))
                };
                await Task.Run(() => SdCardSync.ApplyAsync(fs, serial, plan, image, zipPath, hasher, applying, CancellationToken.None), CancellationToken.None);
                await hashing;
            }, pastPointOfNoReturn ? CancellationToken.None : token);

            Update("Firmware", "Installing firmware. Do not unplug either device.", percent: ApplyEnd);
            string version = release?.Version ?? sd.Version;
            if (release == null) { firmware.StartFromArchive(zipPath, version); }
            else { firmware.Start(release); }
            FirmwareProgress progress;
            while (true)
            {
                await Task.Delay(1000, CancellationToken.None);
                progress = firmware.Status;
                Update(item: progress.Message, percent: progress.TotalBytes is > 0 ? Scale(ApplyEnd, 99, progress.Bytes, progress.TotalBytes.Value) : null);
                if (!progress.Active) { break; }
            }
            if (progress.Phase != "Completed") { throw new InvalidOperationException("The SD card was reset, but the firmware install did not finish: " + progress.Message); }
            SaveRecord(new() { Started = DateTimeOffset.UtcNow, PointOfNoReturn = true, Completed = true });
            message = $"Factory reset complete. The RT4K is running firmware {version}.";
        }
        catch (OperationCanceledException) when (!pastPointOfNoReturn)
        {
            message = "Canceled. Nothing on the RT4K's SD card was changed.";
            TrySaveRecord(new() { Completed = true });
        }
        catch (Exception ex)
        {
            failed = true;
            Console.WriteLine("Factory reset: " + ex);
            message = pastPointOfNoReturn
                ? "The factory reset didn't finish: " + ex.Message + " Run it again."
                : "The factory reset stopped before anything changed: " + ex.Message;
            if (!pastPointOfNoReturn) { TrySaveRecord(new() { Completed = true }); }
        }
        finally
        {
            try { DeleteDownload(); } catch (Exception ex) { Console.WriteLine("Factory reset cleanup: " + ex.Message); }
            Program.FuseDaemon?.ResumeShare();
            lock (gate)
            {
                status = status with { Phase = failed ? "Failed" : "Done", Message = message, CurrentItem = null, Active = false, CanCancel = false, Failed = failed, Percent = failed ? status.Percent : (message.StartsWith("Canceled") ? status.Percent : 100) };
                cancellation?.Dispose();
                cancellation = null;
            }
        }
    }

    private async Task<int> RequireDeviceAsync(CancellationToken token)
    {
        if (Program.RT4K is RT4K rt4k && rt4k.Power == RT4K.PowerState.Off)
        {
            rt4k.PowerOn();
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        for (int attempt = 0; ; attempt++)
        {
            var fields = new Dictionary<string, string>();
            foreach (string line in await serial.SendCommandAsync("status", l => l.StartsWith("status oerr="), 5000, token: token))
            {
                if (line.StartsWith("status ")) { foreach (var pair in Serial.ParseFields(line)) { fields[pair.Key] = pair.Value; } }
            }
            if (int.TryParse(fields.GetValueOrDefault("model"), out int model) && model is 0 or 1 && fields.GetValueOrDefault("sd") == "1") { return model; }
            if (attempt >= 4) { throw new InvalidOperationException("An awake RT4K Pro or CE with an SD card is required."); }
            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    private void DeleteDownload()
    {
        if (Directory.Exists(downloadDirectory)) { Directory.Delete(downloadDirectory, true); }
    }

    private void SaveRecord(FactoryResetRecord record)
    {
        string temporary = recordPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, FactoryResetJsonContext.Default.FactoryResetRecord));
        File.Move(temporary, recordPath, true);
    }

    private void TrySaveRecord(FactoryResetRecord record)
    {
        try { SaveRecord(record); } catch (Exception ex) { Console.WriteLine("Factory reset record: " + ex.Message); }
    }
}
