namespace rt4k_pi;

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public record FirmwareRelease(string Id, string Version, string Date, bool Experimental, string Download, string Sha256, string Changelog);
public record FirmwareImage(ZipArchiveEntry Entry, string Name, string Sha256);

public sealed class FirmwareCatalog(HttpClient http, TimeProvider? time = null)
{
    public const long MaxDownloadBytes = 64 * 1024 * 1024;
    public const long MaxImageBytes = 16 * 1024 * 1024;
    public const string Site = "https://retrotink-llc.github.io/firmware/";
    private readonly HttpClient http = http;
    private readonly TimeProvider time = time ?? TimeProvider.System;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Snapshot(FirmwareRelease[] Releases, DateTimeOffset Fetched);
    private Snapshot? cached;

    public FirmwareRelease? FindUpdate(string? currentVersion, bool includeExperimental)
    {
        Snapshot? snapshot = Volatile.Read(ref cached);
        if (snapshot == null || time.GetUtcNow() - snapshot.Fetched >= CacheLifetime ||
            !Version.TryParse(currentVersion, out var current)) { return null; }
        if (current.Build < 0) { current = new Version(current.Major, current.Minor, 0); }
        return snapshot.Releases.FirstOrDefault(release =>
            (includeExperimental || !release.Experimental) && FirmwareUpdater.IsSupportedVersion(release.Version) &&
            Version.Parse(release.Version) > current);
    }

    public async Task<FirmwareRelease[]> GetAsync(CancellationToken token, bool forceRefresh = false)
    {
        await gate.WaitAsync(token);
        try
        {
            Snapshot? snapshot = Volatile.Read(ref cached);
            if (!forceRefresh && snapshot != null && time.GetUtcNow() - snapshot.Fetched < CacheLifetime) { return snapshot.Releases; }
            var releases = new List<FirmwareRelease>();
            foreach (bool experimental in new[] { false, true })
            {
                using var response = await http.GetAsync(Site + (experimental ? "4k-experimental.html" : "4k.html"), HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                using var output = new MemoryStream();
                using var input = await response.Content.ReadAsStreamAsync(token);
                await CopyBoundedAsync(input, output, 2 * 1024 * 1024, null, token);
                releases.AddRange(Parse(Encoding.UTF8.GetString(output.ToArray()), experimental));
            }
            FirmwareRelease[] sorted = [.. releases.OrderByDescending(r => Version.Parse(r.Version)).ThenBy(r => r.Experimental)];
            Volatile.Write(ref cached, new Snapshot(sorted, time.GetUtcNow()));
            return sorted;
        }
        finally { gate.Release(); }
    }

    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static FirmwareRelease[] Parse(string html, bool experimental)
    {
        var result = new List<FirmwareRelease>();
        foreach (Match section in Matches(html, @"<h2\b[^>]*>\s*Version\s+(?<version>\d+\.\d+\.\d+)\s+\((?<date>\d{4}-\d{2}-\d{2})\)</h2>(?<body>.*?)(?=<h2\b|</section>|\z)"))
        {
            string body = section.Groups["body"].Value;
            var links = Matches(body, "href=\"(?<url>[^\"]+\\.zip)\"");
            var hashes = Matches(body, @"SHA-256:\s*<code\b[^>]*>\s*(?<hash>[a-f0-9]{64})\s*</code>");
            var notes = Matches(body, @"<h3\b[^>]*>Changelog:?</h3>(?<notes>.*)");
            if (links.Count != 1 || hashes.Count != 1 || notes.Count != 1) { throw new InvalidDataException("The official firmware listing has an unrecognized format. No update was started."); }
            string url = WebUtility.HtmlDecode(links[0].Groups["url"].Value);
            ValidateDownload(url);
            string version = section.Groups["version"].Value;
            if (!Version.TryParse(version, out _)) { throw new InvalidDataException("Invalid release version."); }
            string text = Regex.Replace(notes[0].Groups["notes"].Value, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(2));
            text = Regex.Replace(text, @"<li\b[^>]*>\s*", "\n• ", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            text = Regex.Replace(text, @"</(?:li|p|ul|ol)>|<br\s*/?>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            text = Regex.Replace(text, "<[^>]*>", "", RegexOptions.None, TimeSpan.FromSeconds(2));
            text = WebUtility.HtmlDecode(text).Trim();
            text = Regex.Replace(text, @"[ \t]*\n(?:[ \t]*\n)*[ \t]*", "\n", RegexOptions.None, TimeSpan.FromSeconds(2));
            result.Add(new((experimental ? "experimental-" : "release-") + version, version, section.Groups["date"].Value, experimental, url, hashes[0].Groups["hash"].Value.ToLowerInvariant(), text));
        }
        if (result.Count == 0 || result.Select(r => r.Id).Distinct().Count() != result.Count) { throw new InvalidDataException("The official firmware listing is empty or ambiguous."); }
        return [.. result];
    }

    public static void ValidateDownload(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "cdn.jsdelivr.net" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !Regex.IsMatch(uri.AbsolutePath, @"\A/gh/retrotink-llc/firmware@(main|[0-9a-fA-F]{7,40})/RetroTINK-4K/[a-zA-Z0-9_./-]+\.zip\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new InvalidDataException("The release does not have a recognized official download URL.");
        }
    }

    public async Task DownloadAsync(FirmwareRelease release, string path, Action<long, long?> progress, CancellationToken token)
    {
        ValidateDownload(release.Download);
        using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        ValidateDownload(response.RequestMessage!.RequestUri!.AbsoluteUri);
        long? length = response.Content.Headers.ContentLength;
        if (length is > MaxDownloadBytes) { throw new InvalidDataException("Firmware ZIP exceeds the download limit."); }
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < (length ?? MaxDownloadBytes) + 8 * 1024 * 1024) { throw new IOException("Not enough free space on the Pi for the firmware ZIP."); }
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
        using var input = await response.Content.ReadAsStreamAsync(token);
        await CopyBoundedAsync(input, output, MaxDownloadBytes, done => progress(done, length), token);
        if (length.HasValue && output.Length != length.Value) { throw new InvalidDataException("Incomplete firmware download."); }
        output.Position = 0;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(output, token));
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("Firmware ZIP SHA-256 does not match the official listing."); }
    }

    public static async Task<FirmwareImage[]> SelectImagesAsync(ZipArchive zip, int model, string version, CancellationToken token)
    {
        string prefix = model switch { 0 => "rt4k_", 1 => "rt4kce_", _ => throw new InvalidDataException("Only positively identified RT4K Pro and RT4K CE devices are supported.") };
        if (zip.Entries.Count > 128 || zip.Entries.Sum(e => e.Length) > 128 * 1024 * 1024) { throw new InvalidDataException("Firmware archive is too large."); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            if (name.Contains('\\') || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "." or "..") || !names.Add(name)) { throw new InvalidDataException("Unsafe or duplicate firmware archive path."); }
        }
        var bins = zip.Entries.Where(e => e.FullName.Equals("rt4kup.bin", StringComparison.OrdinalIgnoreCase)).ToArray();
        var rbfs = zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".rbf", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/')).ToArray();
        if (bins.Length != 1 || rbfs.Length != 1) { throw new InvalidDataException("The archive does not contain exactly one update binary and one FPGA image for this model."); }
        if (!rbfs[0].FullName.Equals(prefix + version.Replace(".", "") + ".rbf", StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The FPGA filename does not match the selected firmware version."); }
        var images = new List<FirmwareImage>();
        foreach (var entry in new[] { rbfs[0], bins[0] })
        {
            if (entry.Length is <= 0 or > MaxImageBytes || !Regex.IsMatch(entry.FullName, @"\A[a-zA-Z0-9_.-]+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) { throw new InvalidDataException("Invalid firmware image."); }
            using var stream = entry.Open();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long length = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, token)) != 0)
            {
                length += count;
                if (length > entry.Length) { throw new InvalidDataException("Firmware image exceeds its declared size."); }
                digest.AppendData(buffer, 0, count);
            }
            if (length != entry.Length) { throw new InvalidDataException("Incomplete firmware image."); }
            string hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            images.Add(new(entry, entry.FullName.ToLowerInvariant(), hash));
        }
        using (var stream = bins[0].Open())
        {
            byte[] header = new byte[128];
            await stream.ReadExactlyAsync(header, token);
            string imageVersion = Encoding.ASCII.GetString(header, 64, 64).TrimEnd('\0');
            if (imageVersion != version) { throw new InvalidDataException("The update binary version does not match the selected release."); }
        }
        return [.. images];
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, Action<long>? progress, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            total += count;
            if (total > limit) { throw new InvalidDataException("Download exceeds its size limit."); }
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            progress?.Invoke(total);
        }
    }
}
