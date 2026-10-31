namespace rt4k_pi;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public record SdCardRelease(string Version, string Date, string Download, string Sha256);

public sealed class SdCardCatalog(HttpClient http)
{
    public const long MaxDownloadBytes = 128 * 1024 * 1024;
    public const string Page = FirmwareCatalog.Site + "4k-sdcards.html";

    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>Always fetches fresh: the SD card listing is not cached.</summary>
    public async Task<SdCardRelease[]> GetAsync(CancellationToken token)
    {
        using var response = await http.GetAsync(Page, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > 2 * 1024 * 1024) { throw new InvalidDataException("The SD card listing is too large."); }
            output.Write(buffer, 0, count);
        }
        return Parse(Encoding.UTF8.GetString(output.ToArray()));
    }

    public static SdCardRelease[] Parse(string html)
    {
        var result = new List<SdCardRelease>();
        foreach (Match section in Matches(html, @"<h2\b[^>]*>\s*Version\s+(?<version>\d+\.\d+\.\d+)\s+\((?<date>\d{4}-\d{2}-\d{2})\)</h2>(?<body>.*?)(?=<h2\b|</section>|\z)"))
        {
            string body = section.Groups["body"].Value;
            var links = Matches(body, "href=\"(?<url>[^\"]+\\.zip)\"");
            var hashes = Matches(body, @"SHA-256:\s*<code\b[^>]*>\s*(?<hash>[a-f0-9]{64})\s*</code>");
            if (links.Count != 1 || hashes.Count != 1) { throw new InvalidDataException("The official SD card listing has an unrecognized format."); }
            string url = WebUtility.HtmlDecode(links[0].Groups["url"].Value);
            ValidateDownload(url);
            result.Add(new(section.Groups["version"].Value, section.Groups["date"].Value, url, hashes[0].Groups["hash"].Value.ToLowerInvariant()));
        }
        if (result.Count == 0) { throw new InvalidDataException("The official SD card listing is empty."); }
        return [.. result.OrderByDescending(r => Version.Parse(r.Version))];
    }

    public static void ValidateDownload(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "cdn.jsdelivr.net" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !Regex.IsMatch(uri.AbsolutePath, @"\A/gh/retrotink-llc/firmware@(main|[0-9a-fA-F]{7,40})/RetroTINK-4K/SD%20card%20images/[a-zA-Z0-9_.-]+\.zip\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new InvalidDataException("The SD card image does not have a recognized official download URL.");
        }
    }

    /// <summary>Downloads to <paramref name="path"/>, hashing while streaming so verification finishes with the download.</summary>
    public async Task DownloadAsync(SdCardRelease release, string path, Action<long, long?> progress, CancellationToken token)
    {
        ValidateDownload(release.Download);
        using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        ValidateDownload(response.RequestMessage!.RequestUri!.AbsoluteUri);
        long? length = response.Content.Headers.ContentLength;
        if (length is > MaxDownloadBytes) { throw new InvalidDataException("The SD card image exceeds the download limit."); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < (length ?? MaxDownloadBytes) + 16 * 1024 * 1024) { throw new IOException("Not enough free space on the Pi for the SD card image."); }
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            using var input = await response.Content.ReadAsStreamAsync(token);
            byte[] buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                total += count;
                if (total > MaxDownloadBytes) { throw new InvalidDataException("Download exceeds its size limit."); }
                digest.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                progress(total, length);
            }
            if (length.HasValue && total != length.Value) { throw new InvalidDataException("Incomplete SD card image download."); }
        }
        string hash = Convert.ToHexString(digest.GetHashAndReset());
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The SD card image does not match the official listing."); }
    }
}
