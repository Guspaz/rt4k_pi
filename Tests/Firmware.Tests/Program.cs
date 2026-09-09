using System.IO.Compression;
using System.Net;
using System.Text.Json;
using FirmwareTests;
using rt4k_pi;

int passed = 0, failed = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}
void Check(bool condition, string message)
{
    if (!condition) { throw new Exception(message); }
}
async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
Task<FirmwareImage[]> Select(byte[] bytes, int model = 1, string version = Data.Version)
{
    return Run();
    async Task<FirmwareImage[]> Run()
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return await FirmwareCatalog.SelectImagesAsync(zip, model, version, CancellationToken.None);
    }
}

await Test("Production entry point and assembly exclude regression sources", () =>
{
    var assembly = typeof(Serial).Assembly;
    Check(assembly.EntryPoint?.DeclaringType?.FullName == "rt4k_pi.Program", "Regression sources replaced the application's entry point.");
    Check(assembly.GetType("FirmwareTests.Fixture") == null, "Test fixtures leaked into the production assembly.");
    return Task.CompletedTask;
});

await Test("Unsupported firmware targets are rejected before update side effects", async () =>
{
    foreach (string version in new[] { "1.9.6", "1.71.999", "1.72.0", "1.74.999", "", "invalid" })
    {
        await using var f = new Fixture();
        bool downloaded = false;
        f.BeforeDownload = _ => { downloaded = true; return Task.CompletedTask; };
        await Reject<InvalidOperationException>(() => Task.Run(() => f.Updater.Start(f.Release with { Version = version })));
        Check(!FirmwareUpdater.IsSupportedVersion(version), "Unsupported version passed numeric validation: " + version);
        Check(!downloaded && f.Device.Commands.Count == 0 && f.Device.Uploads.Count == 0 && f.Device.GoCount == 0, "Rejected target caused download or device access.");
        Check(f.Updater.Status.Phase == "Idle" && !f.Updater.Status.Active && !File.Exists(Path.Combine(f.DirectoryPath, "journal.json")), "Rejected target created an operation or journal.");
    }
    Check(!FirmwareUpdater.IsSupportedVersion(null), "Missing version was accepted.");
});

await Test("Minimum and newer firmware targets are accepted using numeric comparison", async () =>
{
    Version minimum = FirmwareUpdater.MinimumSupportedVersion;
    var versions = new List<string> { minimum.ToString(), $"{minimum.Major}.{minimum.Minor}.{minimum.Build + 1}", $"{minimum.Major}.{minimum.Minor + 25}.0", $"{minimum.Major + 1}.0.0" };
    if (minimum.Build == 0 && minimum.Revision <= 0) { versions.Add($"{minimum.Major}.{minimum.Minor}"); }
    foreach (string version in versions)
    {
        Check(FirmwareUpdater.IsSupportedVersion(version), "Supported version rejected: " + version);
        await using var f = new Fixture();
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeDownload = async token => { downloading.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        f.Updater.Start(f.Release with { Version = version });
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Updater.Cancel(), "Accepted test update could not be canceled.");
        await f.IdleAsync();
        Check(f.Updater.Status.Phase == "Canceled" && f.Device.GoCount == 0, f.Updater.Status.Message);
    }
});

await Test("Release API serializes the centrally defined minimum version", () =>
{
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(new FirmwareListing([]), FirmwareJsonContext.Default.FirmwareListing));
    Check(json.RootElement.GetProperty("minimumSupportedVersion").GetString() == FirmwareUpdater.MinimumSupportedVersion.ToString(), "API minimum drifted from server validation.");
    return Task.CompletedTask;
});

await Test("Catalog parses changelog as text and rejects missing integrity metadata", async () =>
{
    var releases = FirmwareCatalog.Parse(Data.Html(), true);
    Check(releases.Length == 1 && releases[0].Experimental && releases[0].Changelog.Contains("test change & another") && !releases[0].Changelog.Contains("<li>"), "Catalog fields or text conversion incorrect.");
    Check(releases[0].Changelog.StartsWith("• A test change"), "Changelog lost its bullet marker.");
    var notes = FirmwareCatalog.Parse(Data.Html(notes: "<ul><li>First</li><li><strong>Second</strong> &amp; more</li></ul>"), true)[0].Changelog;
    Check(notes.Contains("• First") && notes.Contains("• Second & more") && !notes.Contains('<'), "Multiple list items were not preserved as safe text.");
    var spaced = FirmwareCatalog.Parse(Data.Html(notes: "\n<ul>\n  <li>First</li>\n\n  <li>Second\n wrapped</li>\n</ul>"), true)[0].Changelog;
    Check(spaced == "• First\n• Second wrapped", "HTML formatting created blank lines between bullets.");
    await Reject<InvalidDataException>(() => Task.Run(() => FirmwareCatalog.Parse(Data.Html().Replace("SHA-256", "Missing"), true)));
    await Reject<InvalidDataException>(() => Task.Run(() => FirmwareCatalog.Parse(Data.Html() + Data.Html(), true)));
    await Reject<InvalidDataException>(() => Task.Run(() => FirmwareCatalog.ValidateDownload("https://evil.example/firmware.zip")));
    await Reject<InvalidDataException>(() => Task.Run(() => FirmwareCatalog.ValidateDownload(Data.Url.Replace("https:", "http:"))));
    FirmwareCatalog.ValidateDownload(Data.Url.Replace("@main/", "@05b0e1a/"));
    await Reject<InvalidDataException>(() => Task.Run(() => FirmwareCatalog.ValidateDownload(Data.Url.Replace("retrotink-llc", "other-owner"))));
});

await Test("Catalog sorts semantic versions and caches official listings", async () =>
{
    int requests = 0;
    using var http = new HttpClient(new Handler((request, _) =>
    {
        requests++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("4k.html") ? Data.Html("1.9.6") : Data.Html() + Data.Html("1.75.0")) });
    }));
    var catalog = new FirmwareCatalog(http);
    var releases = await catalog.GetAsync(CancellationToken.None);
    await catalog.GetAsync(CancellationToken.None);
    Check(releases[0].Version == "1.77.0" && releases[^1].Version == "1.9.6" && requests == 2, "Semantic ordering or cache failed.");
});

foreach (int model in new[] { 0, 1 })
{
    await Test($"Archive selects only model {model} and the shared binary", async () =>
    {
        var images = await Select(Data.Zip(Data.Images()), model);
        Check(images.Length == 2 && images[0].Name == (model == 0 ? "rt4k_1770.rbf" : "rt4kce_1770.rbf") && images[1].Name == "rt4kup.bin", "Wrong firmware selected.");
    });
}

await Test("Archive fails closed on unknown model, missing/wrong/ambiguous images, and version mismatch", async () =>
{
    byte[] archive = Data.Zip(Data.Images());
    await Reject<InvalidDataException>(() => Select(archive, 2));
    await Reject<InvalidDataException>(() => Select(archive, version: "1.76.0"));
    var missing = Data.Images(); missing.Remove("rt4kce_1770.rbf");
    await Reject<InvalidDataException>(() => Select(Data.Zip(missing)));
    var ambiguous = Data.Images(); ambiguous["rt4kce_extra.rbf"] = [1];
    await Reject<InvalidDataException>(() => Select(Data.Zip(ambiguous)));
    var badBinary = Data.Images(); badBinary["rt4kup.bin"] = Data.Binary("1.76.0");
    await Reject<InvalidDataException>(() => Select(Data.Zip(badBinary)));
});

await Test("Archive rejects traversal, duplicate names and oversized images", async () =>
{
    var unsafeEntries = Data.Images(); unsafeEntries["../escape"] = [1];
    await Reject<InvalidDataException>(() => Select(Data.Zip(unsafeEntries)));
    var duplicates = Data.Images().Append(new("RT4KUP.BIN", [2]));
    await Reject<InvalidDataException>(() => Select(Data.Zip(duplicates)));
    var oversized = Data.Images(); oversized["rt4kce_1770.rbf"] = new byte[FirmwareCatalog.MaxImageBytes + 1];
    await Reject<InvalidDataException>(() => Select(Data.Zip(oversized)));
});

foreach (int model in new[] { 0, 1 })
{
    await Test($"Model {model} full update: correct files, ordered publication, single noncancelable flash", async () =>
    {
        await using var f = new Fixture(model);
        f.Device.AfterCommand = (command, _) =>
        {
            if (command.StartsWith("fwup go "))
            {
                Check(!f.Updater.Status.CanCancel && !f.Updater.Cancel(), "Flash boundary reopened cancellation.");
                var disk = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(f.DirectoryPath, "journal.json")), FirmwareJsonContext.Default.FirmwareJournal)!;
                Check(disk.FlashStarted, "Flash command preceded durable journal intent.");
            }
            return Task.CompletedTask;
        };
        await f.RunAsync();
        Check(f.Updater.Status.Phase == "Completed", f.Updater.Status.Message);
        Check(f.Device.Uploads.SequenceEqual(new[] { model == 0 ? "rt4k_1770.rbf" : "rt4kce_1770.rbf", "rt4kup.bin" }), "Unnecessary/wrong files sent, or incorrect order.");
        Check(f.Device.Commands.Count(c => c == "fwup check") == 1 && !f.Device.Commands.Any(c => c.StartsWith("sha256 ") || c.StartsWith("mv ") || c.StartsWith("rm ")), "Redundant device validation or staging commands.");
        Check(f.Device.GoCount == 1 && f.Device.Files.ContainsKey("profiles/user.rt4") && f.Device.Files.ContainsKey(model == 0 ? "rt4k_1750.rbf" : "rt4kce_1750.rbf"), "Old firmware/user files lost or flash repeated.");
        Check(!f.Device.Files.Keys.Any(k => k.StartsWith('.')) && !File.Exists(Path.Combine(f.DirectoryPath, "download.zip")), "Temporary files leaked.");
    });
}

await Test("Existing destinations are replaced by verified uploads without hashing", async () =>
{
    await using (var f = new Fixture())
    {
        f.Device.Files["rt4kce_1770.rbf"] = Data.Images()["rt4kce_1770.rbf"];
        await f.RunAsync();
        Check(f.Updater.Status.Phase == "Completed" && f.Device.Uploads.SequenceEqual(new[] { "rt4kce_1770.rbf", "rt4kup.bin" }), f.Updater.Status.Message);
    }
    await using (var f = new Fixture())
    {
        f.Device.Files["rt4kce_1770.rbf"] = [99];
        await f.RunAsync();
        Check(f.Device.GoCount == 1 && f.Device.Files["rt4kce_1770.rbf"].SequenceEqual(Data.Images()["rt4kce_1770.rbf"]), "Verified replacement was not committed.");
    }
});

await Test("Bad ZIP hash and HTTP errors never touch device firmware", async () =>
{
    await using var f = new Fixture();
    await f.RunAsync(f.Release with { Sha256 = new string('0', 64) });
    Check(f.Updater.Status.Phase == "Failed" && f.Device.Uploads.Count == 0 && f.Device.GoCount == 0 && !File.Exists(Path.Combine(f.DirectoryPath, "download.zip")), f.Updater.Status.Message);
    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
    await Reject<HttpRequestException>(() => new FirmwareCatalog(http).DownloadAsync(f.Release, Path.Combine(f.DirectoryPath, "missing.zip"), (_, _) => { }, CancellationToken.None));
});

foreach (string fault in new[] { "model", "sd", "space" })
{
    await Test("Preflight rejects " + fault, async () =>
    {
        await using var f = new Fixture();
        if (fault == "model") { f.Device.Model = 2; }
        if (fault == "sd") { f.Device.SdPresent = false; }
        if (fault == "space") { f.Device.FreeKiB = 0; }
        await f.RunAsync();
        Check(f.Device.GoCount == 0 && f.Device.Uploads.Count == 0 && f.Updater.Status.Phase == "Failed", f.Updater.Status.Message);
    });
}

await Test("Download is cancelable and concurrent starts are rejected", async () =>
{
    await using var f = new Fixture();
    var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    f.BeforeDownload = async token => { downloading.SetResult(); await Task.Delay(Timeout.Infinite, token); };
    f.Updater.Start(f.Release);
    await downloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Reject<InvalidOperationException>(() => Task.Run(() => f.Updater.Start(f.Release)));
    Check(f.Updater.Cancel(), "Download cancellation unavailable.");
    await f.IdleAsync();
    Check(f.Updater.Status.Phase == "Canceled" && f.Device.GoCount == 0 && f.Device.Uploads.Count == 0, f.Updater.Status.Message);
});

await Test("Upload cancellation reports progress and cleans protocol scratch files", async () =>
{
    await using var f = new Fixture();
    bool canceled = false;
    f.Device.DuringUpload = (_, _) =>
    {
        if (!canceled)
        {
            Check(f.Updater.Status.Bytes > 0 && f.Updater.Status.TotalBytes > 0, "No transfer progress.");
            canceled = f.Updater.Cancel();
        }
        return Task.CompletedTask;
    };
    await f.RunAsync();
    Check(canceled && f.Updater.Status.Phase == "Canceled" && f.Device.GoCount == 0 && !f.Device.Files.Keys.Any(k => k.StartsWith('.')), f.Updater.Status.Message);
    Check(Data.Hash(f.Device.Files["rt4kup.bin"]) == Data.Hash(Data.Binary(Data.OriginalVersion)), "Original binary changed during cancellation.");
});

foreach (bool originalExists in new[] { false, true })
{
    await Test($"Cancellation after atomic upload retains verified files (original exists={originalExists})", async () =>
    {
        await using var f = new Fixture();
        if (!originalExists) { f.Device.Files.Remove("rt4kup.bin"); }
        f.Device.AfterCommand = (command, _) =>
        {
            if (command == "fwup check") { Check(f.Updater.Cancel(), "Check not cancelable."); }
            return Task.CompletedTask;
        };
        await f.RunAsync();
        Check(f.Updater.Status.Phase == "Canceled" && f.Device.GoCount == 0 && f.Device.Files.ContainsKey("rt4kce_1770.rbf") && !f.Device.Files.Keys.Any(k => k.StartsWith('.')), f.Updater.Status.Message);
        Check(Data.Hash(f.Device.Files["rt4kup.bin"]) == Data.Hash(Data.Binary(Data.Version)), "Completed binary was removed or rolled back.");
    });
}

await Test("Version/token rejection retains verified files without flashing", async () =>
{
    foreach (bool badVersion in new[] { false, true })
    {
        await using var f = new Fixture();
        if (badVersion) { f.Device.FinalCheckVersion = "1.76.0"; }
        else { f.Device.FinalToken = "bad-token\npwr off"; }
        await f.RunAsync();
        Check(f.Device.GoCount == 0 && f.Updater.Status.Phase == "Failed" && !f.Updater.Status.NeedsRecovery && Data.Hash(f.Device.Files["rt4kup.bin"]) == Data.Hash(Data.Binary(Data.Version)), f.Updater.Status.Message);
    }
});

await Test("Lost flash reply is never retried and a reported target version is verified", async () =>
{
    await using var f = new Fixture();
    f.Device.ThrowAfterGo = true;
    await f.RunAsync();
    Check(f.Device.GoCount == 1 && f.Updater.Status.Phase == "Completed", f.Updater.Status.Message);
});

await Test("Unverified flash preserves boot files and recovery never resends go", async () =>
{
    await using var f = new Fixture();
    f.Device.AnswerAfterFlash = false;
    f.Device.ThrowAfterGo = true;
    await f.RunAsync();
    Check(f.Device.GoCount == 1 && f.Updater.Status.NeedsRecovery && f.Device.Files.ContainsKey("rt4kup.bin") && f.Device.Files.ContainsKey("rt4kce_1770.rbf"), f.Updater.Status.Message);
    await Reject<InvalidOperationException>(() => Task.Run(() => f.Updater.Start(f.Release)));
    f.Device.AnswerAfterFlash = true;
    await f.Updater.CleanupInterruptedAsync();
    await f.IdleAsync();
    Check(f.Device.GoCount == 1 && f.Updater.Status.Phase == "Completed" && !f.Updater.Status.NeedsRecovery, f.Updater.Status.Message);
});

for (int point = 0; point < 6; point++)
{
    int crashPoint = point;
    await Test("Restart reconciliation at publication/rollback boundary " + point, async () =>
    {
        await using var f = new Fixture();
        var journal = f.RecoveryJournal(publication: crashPoint > 0);
        f.WriteJournal(journal);
        f.Device.Files[journal.Rbf] = Data.Images()[journal.Rbf];
        if (crashPoint <= 2) { f.Device.Files[".rt4k-pi-update.bin"] = Data.Binary(Data.Version); }
        if (crashPoint is 2 or 3 or 4)
        {
            f.Device.Files[".rt4k-pi-backup.bin"] = f.Device.Files["rt4kup.bin"];
            f.Device.Files.Remove("rt4kup.bin");
        }
        if (crashPoint == 3) { f.Device.Files["rt4kup.bin"] = Data.Binary(Data.Version); }
        File.WriteAllText(Path.Combine(f.DirectoryPath, "download.zip"), "incomplete");
        var files = f.Device.Files.ToDictionary(pair => pair.Key, pair => Data.Hash(pair.Value));
        _ = f.Updater;
        Check(!File.Exists(Path.Combine(f.DirectoryPath, "download.zip")), "Startup did not clean local ZIP.");
        await f.Updater.CleanupInterruptedAsync();
        await f.IdleAsync();
        Check(f.Updater.Status.NeedsRecovery == (crashPoint > 0) && f.Device.Commands.Count == 0 && f.Device.Uploads.Count == 0, f.Updater.Status.Message);
        Check(files.All(pair => Data.Hash(f.Device.Files[pair.Key]) == pair.Value) && files.Count == f.Device.Files.Count, "Legacy files were changed without ownership verification.");
    });
}

await Test("Unsupported fwup stops before flashing after verified transfers", async () =>
{
    await using var f = new Fixture();
    f.Device.SupportsFwup = false;
    await f.RunAsync();
    Check(f.Device.GoCount == 0 && f.Updater.Status.Phase == "Failed" && f.Device.Commands.Count(c => c == "fwup check") == 1, f.Updater.Status.Message);
});

await Test("Post-flash restart recovery verifies rather than reflashes", async () =>
{
    await using var f = new Fixture();
    var journal = f.RecoveryJournal(publication: true, flashed: true);
    f.WriteJournal(journal);
    f.Device.Version = Data.Version;
    f.Device.Files[journal.Rbf] = Data.Images()[journal.Rbf];
    f.Device.Files[".rt4k-pi-backup.bin"] = f.Device.Files["rt4kup.bin"];
    f.Device.Files["rt4kup.bin"] = Data.Binary(Data.Version);
    await f.Updater.CleanupInterruptedAsync();
    await f.IdleAsync();
    Check(f.Updater.Status.Phase == "Completed" && f.Device.GoCount == 0 && f.Device.Files.ContainsKey(journal.Rbf) && f.Device.Files.ContainsKey(".rt4k-pi-backup.bin"), f.Updater.Status.Message);
});

await Test("Interrupted flash waits for the original model and target version", async () =>
{
    foreach (bool differentModel in new[] { false, true })
    {
        await using var f = new Fixture();
        f.WriteJournal(f.RecoveryJournal(flashed: true));
        if (differentModel) { f.Device.Model = 0; }
        else { f.Device.Files[".rt4k-pi-update.bin"] = [9]; }
        await f.Updater.CleanupInterruptedAsync();
        await f.IdleAsync();
        Check(f.Updater.Status.NeedsRecovery && f.Device.GoCount == 0 && !f.Device.Commands.Any(c => c.StartsWith("rm ") || c.StartsWith("mv ")), "Unsafe cleanup proceeded.");
    }
});

await Test("Changed serial connection before flash stops the update for cleanup", async () =>
{
    await using var f = new Fixture();
    bool changed = false;
    f.Device.DuringUpload = (_, _) => { if (!changed) { f.Device.ConnectionRevision++; changed = true; } return Task.CompletedTask; };
    await f.RunAsync();
    Check(f.Updater.Status.Phase == "Failed" && !f.Updater.Status.NeedsRecovery && f.Device.GoCount == 0, "Update continued after device replacement.");
    await f.Updater.CleanupInterruptedAsync();
    await f.IdleAsync();
    Check(!f.Updater.Status.NeedsRecovery && f.Device.GoCount == 0, f.Updater.Status.Message);
});

await Test("Corrupt/unsafe journals fail closed while local ZIP is cleaned", async () =>
{
    foreach (bool invalidJson in new[] { false, true })
    {
        await using var f = new Fixture();
        if (invalidJson) { File.WriteAllText(Path.Combine(f.DirectoryPath, "journal.json"), "{bad"); }
        else { f.WriteJournal(f.RecoveryJournal() with { Rbf = "../user-file" }); }
        File.WriteAllText(Path.Combine(f.DirectoryPath, "download.zip"), "incomplete");
        Check(f.Updater.Status.NeedsRecovery && !File.Exists(Path.Combine(f.DirectoryPath, "download.zip")), "Corrupt journal leaked ZIP or allowed update.");
        await f.Updater.CleanupInterruptedAsync();
        Check(f.Updater.Status.NeedsRecovery && !f.Updater.Status.Active, "Corrupt records must remain blocked.");
        Check(f.Device.Commands.Count == 0, "Corrupt journal reached device commands.");
    }
});

await Test("Failed initial journal write reports failure without starting work", async () =>
{
    await using var f = new Fixture();
    var updater = f.Updater;
    string obstruction = Path.Combine(f.DirectoryPath, "journal.new");
    Directory.CreateDirectory(obstruction);
    await Reject<IOException>(() => Task.Run(() => updater.Start(f.Release)));
    Check(updater.Status.Phase == "Failed" && !updater.Status.Active && !updater.Status.CanCancel, "Failed start left a phantom Preparing operation.");
    Check(f.Device.Commands.Count == 0 && f.Device.Uploads.Count == 0, "Failed start touched the RT4K.");
    Directory.Delete(obstruction);
    await f.RunAsync();
    Check(updater.Status.Phase == "Completed", updater.Status.Message);
});

await Test("Untouched interrupted updates discard downloads without resuming", async () =>
{
    await using var f = new Fixture();
    f.WriteJournal(new FirmwareJournal { Version = Data.Version });
    File.WriteAllText(Path.Combine(f.DirectoryPath, "download.zip"), "partial");
    Check(f.Updater.Status.Phase == "Canceled" && !f.Updater.Status.NeedsRecovery, "Untouched update was not canceled.");
    Check(!File.Exists(Path.Combine(f.DirectoryPath, "download.zip")) && f.Device.Commands.Count == 0, "Startup resumed work or leaked download.");
});

await Test("Background cleanup waits for connection and never resumes installation", async () =>
{
    await using var f = new Fixture();
    f.WriteJournal(f.RecoveryJournal(flashed: true));
    f.Device.IsConnected = false;
    await f.Updater.CleanupInterruptedAsync();
    Check(f.Updater.Status.Phase == "WaitingForDevice" && !f.Updater.Status.Active && !f.Updater.Status.CanCancel, "Disconnected cleanup misreported work.");
    f.Device.IsConnected = true;
    f.Device.Version = Data.Version;
    f.Updater.StartCleanupMonitor();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (f.Updater.Status.NeedsRecovery || f.Updater.Status.Active) { await Task.Delay(5, timeout.Token); }
    Check(f.Updater.Status.Phase == "Completed" && f.Device.GoCount == 0 && f.Device.Uploads.Count == 0, "Cleanup resumed installation.");
});

await Test("Serial streaming without ACKs, sequence wrap and verified completion", SerialChecks.TransferAsync);
await Test("Streaming upload rejects device verification failure", SerialChecks.VerificationFailureAsync);
await Test("Suppressed informational logs do not leak blank lines", SerialChecks.LoggingAsync);
await Test("NUL reboot noise is filtered without losing valid serial text", SerialChecks.RebootTextAsync);
await Test("Serial cancellation sends ABORT, drains before releasing, and never sends EOF", SerialChecks.CancelAsync);

if (args.Contains("--live"))
{
    await Test("Live official listings and latest stable/experimental Pro+CE packages", async () =>
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var catalog = new FirmwareCatalog(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var releases = await catalog.GetAsync(timeout.Token);
        Console.WriteLine($"Official catalog: {releases.Length} releases; latest {releases[0].Version}.");
        await using var f = new Fixture();
        foreach (var release in new[] { releases.First(r => !r.Experimental), releases.First(r => r.Experimental) })
        {
            string path = Path.Combine(f.DirectoryPath, "live.zip");
            await catalog.DownloadAsync(release, path, (_, _) => { }, timeout.Token);
            using (var zip = ZipFile.OpenRead(path))
            {
                foreach (int model in new[] { 0, 1 })
                {
                    var images = await FirmwareCatalog.SelectImagesAsync(zip, model, release.Version, timeout.Token);
                    Console.WriteLine($"Validated {release.Version}, model {model}: {string.Join(", ", images.Select(i => i.Name))}");
                }
            }
            File.Delete(path);
        }
    });
}

Console.WriteLine($"Firmware regression results: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
