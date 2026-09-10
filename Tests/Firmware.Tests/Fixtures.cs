namespace FirmwareTests;

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using rt4k_pi;

internal static class Data
{
    public const string Version = "1.77.0";
    public const string OriginalVersion = "1.75.0";
    public const string Url = "https://cdn.jsdelivr.net/gh/retrotink-llc/firmware@main/RetroTINK-4K/Experimental/rt4k_1770.zip";
    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static byte[] Binary(string version)
    {
        byte[] bytes = new byte[256];
        Encoding.ASCII.GetBytes("RT4K Pro FW Image").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes(version).CopyTo(bytes, 64);
        return bytes;
    }
    public static Dictionary<string, byte[]> Images() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["rt4kup.bin"] = Binary(Version),
        ["rt4k_1770.rbf"] = Enumerable.Range(0, 600).Select(i => (byte)(i % 251)).ToArray(),
        ["rt4kce_1770.rbf"] = Enumerable.Range(0, 300).Select(i => (byte)(i % 241)).ToArray(),
        ["rt6x_1770.rbf"] = [1, 2, 3]
    };
    public static byte[] Zip(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var entry in entries)
            {
                using var output = zip.CreateEntry(entry.Key).Open();
                output.Write(entry.Value);
            }
        }
        return stream.ToArray();
    }
    public static string Html(string version = Version, string notes = "<ul><li>A test change &amp; another change</li></ul>") => $"<section><h2 id=\"version\">Version {version} (2026-08-28)</h2><h3><a href=\"{Url}\">Download</a></h3><p>SHA-256: <code>{new string('a', 64)}</code></p><h3>Changelog:</h3>{notes}</section>";
}

internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var result = await response(request, cancellationToken);
        result.RequestMessage = request;
        return result;
    }
}

internal sealed class CatalogTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FastTime : TimeProvider
{
    public override long GetTimestamp() => Stopwatch.GetTimestamp() * 200;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        TimeSpan Scale(TimeSpan value) => value == Timeout.InfiniteTimeSpan ? value : TimeSpan.FromTicks(Math.Max(1, value.Ticks / 200));
        return System.CreateTimer(callback, state, Scale(dueTime), Scale(period));
    }
}

internal sealed class Fixture : IAsyncDisposable
{
    public readonly string DirectoryPath = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
    public readonly SimulatedDevice Device;
    public readonly byte[] Archive;
    public readonly FirmwareRelease Release;
    public readonly HttpClient Http;
    public readonly FirmwareCatalog Catalog;
    public Func<CancellationToken, Task>? BeforeDownload;
    private FirmwareUpdater? updater;
    public FirmwareUpdater Updater => updater ??= new(Device, Catalog, DirectoryPath, new FastTime());

    public Fixture(int model = 1, byte[]? archive = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        Device = new(model);
        Archive = archive ?? Data.Zip(Data.Images());
        Release = new("experimental-" + Data.Version, Data.Version, "2026-08-28", true, Data.Url, Data.Hash(Archive), "Synthetic test release");
        Http = new(new Handler(async (_, token) =>
        {
            if (BeforeDownload != null) { await BeforeDownload(token); }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) };
        }));
        Catalog = new(Http);
    }

    public async Task RunAsync(FirmwareRelease? release = null)
    {
        Updater.Start(release ?? Release);
        await IdleAsync();
    }

    public async Task IdleAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (Updater.Status.Active) { await Task.Delay(5, timeout.Token); }
    }

    public void WriteJournal(FirmwareJournal journal) => File.WriteAllText(Path.Combine(DirectoryPath, "journal.json"), JsonSerializer.Serialize(journal, FirmwareJsonContext.Default.FirmwareJournal));

    public FirmwareJournal RecoveryJournal(bool publication = false, bool flashed = false) => new()
    {
        Phase = flashed ? "Flashing" : "Transferring",
        Version = Data.Version,
        OriginalVersion = Data.OriginalVersion,
        Model = Device.Model,
        DeviceTouched = true,
        NeedsRecovery = true,
        RbfCreated = true,
        PublicationStarted = publication,
        FlashStarted = flashed,
        Rbf = Device.Model == 0 ? "rt4k_1770.rbf" : "rt4kce_1770.rbf",
        RbfHash = Data.Hash(Data.Images()[Device.Model == 0 ? "rt4k_1770.rbf" : "rt4kce_1770.rbf"]),
        BinHash = Data.Hash(Data.Binary(Data.Version)),
        OriginalBinHash = Data.Hash(Data.Binary(Data.OriginalVersion))
    };

    public async ValueTask DisposeAsync()
    {
        if (updater != null) { await updater.StopAsync(); }
        Http.Dispose();
        Directory.Delete(DirectoryPath, true);
    }
}

internal sealed class SimulatedDevice : IFirmwareDevice
{
    public int Model;
    public string Version = Data.OriginalVersion;
    public bool IsConnected { get; set; } = true;
    public long ConnectionRevision { get; set; } = 1;
    public bool SdPresent = true;
    public bool SupportsFwup = true;
    public long FreeKiB = 100000;
    public bool AnswerAfterFlash = true;
    public bool ThrowAfterGo;
    public string? FinalCheckVersion;
    public string FinalToken = "0A1B2C3D";
    public int GoCount;
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Commands = [];
    public readonly List<string> Uploads = [];
    public Func<string, CancellationToken, Task>? AfterCommand;
    public Func<string, CancellationToken, Task>? DuringUpload;
    private bool exclusive;

    public SimulatedDevice(int model)
    {
        Model = model;
        Files["rt4kup.bin"] = Data.Binary(Data.OriginalVersion);
        Files[model == 0 ? "rt4k_1750.rbf" : "rt4kce_1750.rbf"] = [9, 8, 7];
        Files["profiles/user.rt4"] = [4, 5, 6];
    }

    public async Task ExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        if (exclusive) { throw new InvalidOperationException("Overlapping serial owners."); }
        token.ThrowIfCancellationRequested();
        exclusive = true;
        try { await action(token); }
        finally { exclusive = false; }
    }

    public async Task<List<string>> CommandAsync(string command, Func<string, bool> terminal, int timeoutMs, CancellationToken token)
    {
        if (!exclusive) { throw new InvalidOperationException("Command outside exclusive operation."); }
        if (!IsConnected) { throw new IOException("Device unplugged."); }
        token.ThrowIfCancellationRequested();
        Commands.Add(command);
        string[] parts = command.Split(' ', 2);
        string path = parts.Length == 2 ? parts[1] : "";
        List<string> replies;
        switch (parts[0])
        {
            case "status":
                replies = GoCount > 0 && !AnswerAfterFlash ? [] : [$"status fw={Version} model={Model} sd={(SdPresent ? 1 : 0)} uptime_s={(GoCount > 0 ? 10 : 100000)}", "status oerr=0"];
                break;
            case "fwup" when path == "check":
                string? imageVersion = Files.TryGetValue("rt4kup.bin", out var binary) ? Encoding.ASCII.GetString(binary, 64, 64).TrimEnd('\0') : null;
                replies = !SupportsFwup ? ["Bad Command: fwup"] : imageVersion == null ? ["fwup fail: no valid /rt4kup.bin"] : [$"fwup ok version={(imageVersion == Data.Version ? FinalCheckVersion ?? imageVersion : imageVersion)} token={FinalToken}"];
                break;
            case "fwup" when path.StartsWith("go "):
                GoCount++;
                Version = Data.Version;
                replies = ["fwup: flashing /rt4kup.bin, unit will reboot into the bootloader"];
                break;
            case "stat":
                replies = Files.TryGetValue(path, out var file) ? [$"stat t=F sz={file.Length} mt=0 at=0x20 nm={path}"] : ["stat err=2 NOSUCH"];
                break;
            case "sha256": throw new InvalidOperationException("Firmware updates must rely on put verification, not serial hashing.");
            case "df": replies = [$"df total=100000 KiB free={FreeKiB} KiB"]; break;
            case "mv":
                string[] names = path.Split('|');
                if (!Files.ContainsKey(names[0]) || Files.ContainsKey(names[1])) { replies = ["mv err=64 EXIST"]; break; }
                Files[names[1]] = Files[names[0]];
                Files.Remove(names[0]);
                replies = ["mv ok"];
                break;
            case "rm": replies = [Files.Remove(path) ? "rm ok" : "rm err=2 NOSUCH"]; break;
            default: throw new InvalidOperationException("Unexpected command: " + command);
        }
        if (AfterCommand != null) { await AfterCommand(command, token); }
        if (command.StartsWith("fwup go ") && ThrowAfterGo) { throw new IOException("Lost flash reply."); }
        token.ThrowIfCancellationRequested();
        return replies;
    }

    public async Task UploadAsync(string path, Stream data, long length, string sha256, Action<long> progress, CancellationToken token)
    {
        if (!exclusive) { throw new InvalidOperationException("Upload outside exclusive operation."); }
        Uploads.Add(path);
        using var received = new MemoryStream();
        byte[] buffer = new byte[64];
        while (received.Length < length)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, length - received.Length);
            await data.ReadExactlyAsync(buffer.AsMemory(0, count), token);
            received.Write(buffer, 0, count);
            progress(received.Length);
            if (DuringUpload != null) { await DuringUpload(path, token); }
        }
        token.ThrowIfCancellationRequested();
        byte[] bytes = received.ToArray();
        if (Data.Hash(bytes) != sha256) { throw new InvalidDataException("Upload hash mismatch."); }
        Files[path] = bytes;
        Files.Remove(".rtl1up.tmp");
    }
}
