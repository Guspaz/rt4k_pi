namespace rt4k_pi;

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using rt4k_pi.Filesystem;

public sealed record SdImageFile(string Path, string EntryName, long Size);
public sealed record SdSyncSummary(int Unchanged, int Replace, int Add, int Delete, int Rename);

/// <summary>What the card looks like, as reported by a recursive "ls". Paths use the device's own letter case.</summary>
public sealed class SdDeviceTree
{
    public Dictionary<string, (string Path, long Size)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Truncated { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The official image's contents, with normalized and validated paths.</summary>
public sealed class SdImage
{
    public Dictionary<string, SdImageFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long TotalBytes => Files.Values.Sum(f => f.Size);
}

public sealed class SdSyncPlan
{
    public List<string> DeleteFiles { get; } = [];
    public List<string> DeleteDirectories { get; } = [];
    public List<string> CreateDirectories { get; } = [];
    public List<(string From, string To)> Renames { get; } = [];
    public List<SdImageFile> Uploads { get; } = [];
    public HashSet<string> TruncatedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long FreedBytes { get; set; }
    public SdSyncSummary Summary { get; set; } = new(0, 0, 0, 0, 0);
    public long UploadBytes => Uploads.Sum(u => u.Size);
}

public sealed class SdSyncReporter
{
    public Action<string>? Item { get; init; }
    public Action<long, long>? Hashing { get; init; }
    public Action<long, long>? Applying { get; init; }
}

/// <summary>
/// Makes the RT4K's SD card match an image. Computing the plan never changes the card, so it can
/// be canceled; applying it can't. Device work is strictly one command at a time and must run
/// inside the caller's exclusive serial session.
/// </summary>
public static class SdCardSync
{
    // Deletes, folder creates and renames are weighted as if they were this many bytes of upload.
    public const long OperationWeight = 4096;
    private static readonly char[] InvalidFatChars = ['"', '*', ':', '<', '>', '?', '\\', '|'];

    /// <summary>Root files owned by the firmware updater. They are neither synced nor deleted.</summary>
    public static bool IsFirmwareFile(string path) => !path.Contains('/') &&
        (path.Equals("rt4kup.bin", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rbf", StringComparison.OrdinalIgnoreCase));

    private static bool IsProtected(string path) => path.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || IsFirmwareFile(path);

    public static SdImage LoadImage(ZipArchive zip)
    {
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
        string prefix = "";
        string[] firsts = [.. names.Where(n => n.Length > 0).Select(n => n.Split('/')[0]).Distinct(StringComparer.Ordinal)];
        if (firsts.Length == 1 && names.All(n => n.StartsWith(firsts[0] + "/", StringComparison.Ordinal))) { prefix = firsts[0] + "/"; }

        var image = new SdImage();
        var exact = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/')[prefix.Length..];
            bool directory = name.EndsWith('/');
            name = name.TrimEnd('/');
            if (name.Length == 0) { continue; }
            Validate(name);
            string[] parts = name.Split('/');
            for (int i = 1; i < parts.Length; i++) { AddDirectory(image, string.Join('/', parts[..i]), exact); }
            if (directory) { AddDirectory(image, name, exact); continue; }
            if (IsFirmwareFile(name)) { continue; }
            if (image.Directories.ContainsKey(name) || !image.Files.TryAdd(name, new(name, entry.FullName, entry.Length))) { throw new InvalidDataException("The SD card image contains two entries that differ only in letter case: " + name); }
        }
        foreach (string dir in image.Directories.Keys)
        {
            if (image.Files.ContainsKey(dir)) { throw new InvalidDataException("The SD card image has a file and folder with the same name: " + dir); }
        }
        return image;
    }

    private static void AddDirectory(SdImage image, string path, HashSet<string> exact)
    {
        if (image.Directories.TryGetValue(path, out string? existing))
        {
            if (existing != path && exact.Add(path)) { throw new InvalidDataException("The SD card image contains two folders that differ only in letter case: " + path); }
            return;
        }
        exact.Add(path);
        image.Directories[path] = path;
    }

    private static void Validate(string path)
    {
        if (path.StartsWith('/') || path.Contains("//")) { throw new InvalidDataException("Unsafe path in the SD card image: " + path); }
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.Length > SerialFs.MaxNameLength || part.IndexOfAny(InvalidFatChars) >= 0 ||
                part.Any(c => c < 0x20) || part.EndsWith(' ') || part.EndsWith('.'))
            {
                throw new InvalidDataException("Unsafe path in the SD card image: " + path);
            }
        }
    }

    /// <summary>Lists every folder on the card. Read-only.</summary>
    public static SdDeviceTree Scan(SerialFs fs, Action<string>? item, CancellationToken token)
    {
        fs.InvalidateCache();
        var tree = new SdDeviceTree();
        var pending = new Queue<string>();
        pending.Enqueue("");
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = pending.Dequeue();
            item?.Invoke("Reading " + (dir.Length == 0 ? "the card" : dir));
            var (entries, truncated) = fs.ListWithFlag(dir);
            if (truncated) { tree.Truncated.Add(dir); }
            foreach (var entry in entries)
            {
                if (entry.Name is "." or ".." || entry.Name.Length == 0) { continue; }
                string path = dir.Length == 0 ? entry.Name : dir + "/" + entry.Name;
                if (IsProtected(path)) { continue; }
                if (entry.IsDirectory)
                {
                    if (tree.Directories.TryAdd(path, path)) { pending.Enqueue(path); }
                }
                else { tree.Files[path] = (path, entry.Size); }
            }
        }
        return tree;
    }

    /// <summary>Hashes the image's files on the Pi, priority paths first, publishing each result as it completes.</summary>
    public sealed class PiHasher(string zipPath, SdImage image)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> hashes = new(
            image.Files.Keys.Select(k => KeyValuePair.Create(k, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously))), StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource<IReadOnlyList<string>> order = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ZipPath => zipPath;

        public void Prioritize(IReadOnlyList<string> first) => order.TrySetResult(first);

        public Task<string> HashAsync(string path, CancellationToken token) => hashes[path].Task.WaitAsync(token);

        public Task RunAsync(CancellationToken token) => Task.Run(async () =>
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
                IReadOnlyList<string> first = await order.Task.WaitAsync(token);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in first.Concat(image.Files.Keys))
                {
                    if (!seen.Add(path)) { continue; }
                    token.ThrowIfCancellationRequested();
                    var file = image.Files[path];
                    using var stream = entries[file.EntryName].Open();
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
                    hashes[path].TrySetResult(hash);
                }
            }
            catch (Exception ex)
            {
                foreach (var pending in hashes.Values) { pending.TrySetException(ex); }
                throw;
            }
        }, token);
    }

    // Profiles store a CRC-16 of everything past byte 128 at offset 32, so the first 128 bytes
    // identify the contents. Bitmaps are spot-checked: header and palette, the end, and evenly
    // spaced samples between. That only shows a bitmap is probably unchanged, which is accepted.
    private const int ProfileHeaderBytes = 128;
    private const int BmpEdgeBytes = 2048;
    private const int BmpSampleBytes = 2048;
    private const int BmpSamples = 6;
    private const long BmpMinimumSize = 2 * BmpEdgeBytes + BmpSamples * BmpSampleBytes * 2;

    private static List<(long Offset, int Length)>? QuickCheckRanges(SdImageFile file)
    {
        if (file.Path.EndsWith(".rt4", StringComparison.OrdinalIgnoreCase) && file.Size > ProfileHeaderBytes) { return [(0, ProfileHeaderBytes)]; }
        if (!file.Path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) || file.Size < BmpMinimumSize) { return null; }
        var ranges = new List<(long, int)> { (0, BmpEdgeBytes) };
        long span = file.Size - 2 * BmpEdgeBytes;
        for (int i = 1; i <= BmpSamples; i++) { ranges.Add((BmpEdgeBytes + span * i / (BmpSamples + 1) - BmpSampleBytes / 2, BmpSampleBytes)); }
        ranges.Add((file.Size - BmpEdgeBytes, BmpEdgeBytes));
        return ranges;
    }

    private static bool QuickMatch(SerialFs fs, string devicePath, ZipArchiveEntry entry, List<(long Offset, int Length)> ranges)
    {
        byte[] expected;
        using (var stream = entry.Open())
        {
            long need = ranges[^1].Offset + ranges[^1].Length;
            expected = new byte[need];
            stream.ReadExactly(expected);
        }
        foreach (var (offset, length) in ranges)
        {
            byte[] actual;
            try { actual = fs.Read(devicePath, offset, length); }
            catch (SerialException) { return false; }
            if (!actual.AsSpan().SequenceEqual(expected.AsSpan((int)offset, length))) { return false; }
        }
        return true;
    }

    // Unhashed reads outrun the RT4K's own sha256, so whole-file checks just download and compare
    private const int CompareChunkBytes = 1024 * 1024;

    private static bool FullMatch(SerialFs fs, string devicePath, ZipArchiveEntry entry, long size, CancellationToken token)
    {
        using var stream = entry.Open();
        byte[] expected = new byte[(int)Math.Min(CompareChunkBytes, size)];
        for (long offset = 0; offset < size; offset += expected.Length)
        {
            token.ThrowIfCancellationRequested();
            int length = (int)Math.Min(expected.Length, size - offset);
            stream.ReadExactly(expected, 0, length);
            byte[] actual;
            try { actual = fs.Read(devicePath, offset, length); }
            catch (SerialException) { return false; }
            if (!actual.AsSpan().SequenceEqual(expected.AsSpan(0, length))) { return false; }
        }
        return true;
    }

    /// <summary>Compares the card with the image, checking same-size files on the device. Never changes the card.</summary>
    public static async Task<SdSyncPlan> PlanAsync(SerialFs fs, SdDeviceTree tree, SdImage image, PiHasher hasher, SdSyncReporter reporter, bool fastChecks, CancellationToken token)
    {
        var plan = new SdSyncPlan();
        plan.TruncatedDirectories.UnionWith(tree.Truncated);
        int unchanged = 0, replace = 0, add = 0;

        foreach (var (key, (path, size)) in tree.Files)
        {
            if (!image.Files.ContainsKey(key)) { plan.DeleteFiles.Add(path); plan.FreedBytes += size; }
        }
        foreach (var (key, path) in tree.Directories)
        {
            if (!image.Directories.ContainsKey(key)) { plan.DeleteDirectories.Add(path); }
        }
        // Files under a truncated listing may exist without having been seen; ask for them directly.
        foreach (var file in image.Files.Values)
        {
            int slash = file.Path.LastIndexOf('/');
            string parent = slash < 0 ? "" : file.Path[..slash];
            if (tree.Truncated.Contains(parent) && !tree.Files.ContainsKey(file.Path) && fs.Stat(file.Path) is { IsDirectory: false } found)
            {
                tree.Files[file.Path] = (parent.Length == 0 ? found.Name : parent + "/" + found.Name, found.Size);
            }
        }

        var candidates = image.Files.Values.Where(f => tree.Files.TryGetValue(f.Path, out var d) && d.Size == f.Size).ToList();
        hasher.Prioritize([.. candidates.Select(c => c.Path)]);

        long hashTotal = candidates.Sum(c => c.Size), hashed = 0;
        reporter.Hashing?.Invoke(0, hashTotal);
        int index = 0;
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(hasher.ZipPath);
        var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        foreach (var file in candidates)
        {
            token.ThrowIfCancellationRequested();
            index++;
            string devicePath = tree.Files[file.Path].Path;
            reporter.Item?.Invoke($"({index} of {candidates.Count}) Checking {file.Path}");
            var ranges = fastChecks ? QuickCheckRanges(file) : null;
            bool same = ranges != null
                ? QuickMatch(fs, devicePath, entries[file.EntryName], ranges)
                : FullMatch(fs, devicePath, entries[file.EntryName], file.Size, token);
            if (same) { matched.Add(file.Path); }
            hashed += file.Size;
            reporter.Hashing?.Invoke(hashed, hashTotal);
        }

        foreach (var file in image.Files.Values)
        {
            if (matched.Contains(file.Path))
            {
                unchanged++;
                string devicePath = tree.Files[file.Path].Path;
                if (!string.Equals(devicePath, file.Path, StringComparison.Ordinal) && Leaf(devicePath) != Leaf(file.Path))
                {
                    plan.Renames.Add((ParentFor(file.Path) + Leaf(devicePath), file.Path));
                }
                continue;
            }
            if (tree.Files.TryGetValue(file.Path, out var existing)) { replace++; plan.FreedBytes += existing.Size; }
            else { add++; }
            plan.Uploads.Add(file);
        }

        foreach (string dir in image.Directories.Keys.OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal))
        {
            if (!tree.Directories.TryGetValue(dir, out string? existing)) { plan.CreateDirectories.Add(image.Directories[dir]); }
            else if (Leaf(existing) != Leaf(image.Directories[dir])) { plan.Renames.Insert(plan.Renames.Count(r => image.Directories.ContainsKey(r.To)), (ParentFor(image.Directories[dir]) + Leaf(existing), image.Directories[dir])); }
        }

        // Directories that are removed take their contents with them; only list each deletion once.
        plan.DeleteDirectories.Sort((a, b) => b.Count(c => c == '/').CompareTo(a.Count(c => c == '/')));
        plan.Summary = new(unchanged, replace, add, plan.DeleteFiles.Count + plan.DeleteDirectories.Count, plan.Renames.Count);
        return plan;
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string ParentFor(string path) { int slash = path.LastIndexOf('/'); return slash < 0 ? "" : path[..(slash + 1)]; }

    public static long ApplyUnits(SdSyncPlan plan) =>
        (plan.DeleteFiles.Count + plan.DeleteDirectories.Count + plan.CreateDirectories.Count + plan.Renames.Count + plan.TruncatedDirectories.Count) * OperationWeight + plan.UploadBytes;

    /// <summary>Changes the card. Must not be canceled partway: the token only guards against a lost connection.</summary>
    public static async Task ApplyAsync(SerialFs fs, Serial serial, SdSyncPlan plan, SdImage image, string zipPath, PiHasher hasher, SdSyncReporter reporter, CancellationToken token)
    {
        long total = ApplyUnits(plan), done = 0;
        void Step(long units) { done += units; reporter.Applying?.Invoke(done, total); }
        reporter.Applying?.Invoke(0, total);

        foreach (string path in plan.DeleteFiles)
        {
            reporter.Item?.Invoke("Deleting " + path);
            RemoveIfPresent(fs, path);
            Step(OperationWeight);
        }
        foreach (string path in plan.DeleteDirectories)
        {
            reporter.Item?.Invoke("Deleting " + path);
            DeleteTree(fs, path, image);
            Step(OperationWeight);
        }
        foreach (string dir in plan.TruncatedDirectories.OrderByDescending(d => d.Count(c => c == '/')))
        {
            if (!image.Directories.ContainsKey(dir) && dir.Length != 0) { Step(OperationWeight); continue; }
            reporter.Item?.Invoke("Cleaning " + (dir.Length == 0 ? "the card" : dir));
            CleanDirectory(fs, dir, image);
            Step(OperationWeight);
        }

        long uploadBytes = plan.UploadBytes;
        long largest = plan.Uploads.Count == 0 ? 0 : plan.Uploads.Max(u => u.Size);
        long needed = uploadBytes - plan.FreedBytes + largest + 4 * 1024 * 1024;
        var (_, freeKiB) = fs.DiskFree();
        if ((long)freeKiB * 1024 < needed + needed / 20)
        {
            throw new IOException("The RT4K SD card does not have enough free space for the new files. Use a larger SD card.");
        }

        foreach (string dir in plan.CreateDirectories)
        {
            reporter.Item?.Invoke("Creating folder " + dir);
            try { fs.MakeDirectory(dir); }
            catch (SerialFsException ex) when (ex.Symbol == "EXIST") { }
            Step(OperationWeight);
        }
        foreach (var (from, to) in plan.Renames)
        {
            reporter.Item?.Invoke("Renaming " + to);
            fs.Rename(from, to);
            Step(OperationWeight);
        }

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var file in plan.Uploads)
        {
            reporter.Item?.Invoke("Uploading " + file.Path);
            string hash = serial.SupportsUnhashedTransfers ? "-" : await hasher.HashAsync(file.Path, token);
            var entry = zip.GetEntry(file.EntryName) ?? throw new InvalidDataException("The SD card image changed while it was being installed.");
            long before = done;
            await using (var stream = entry.Open())
            {
                await serial.PutStreamAsync(file.Path, stream, file.Size, hash, sent => reporter.Applying?.Invoke(before + sent, total), token);
            }
            done = before + file.Size;
            reporter.Applying?.Invoke(done, total);
        }
        fs.InvalidateCache();
    }

    private static void RemoveIfPresent(SerialFs fs, string path)
    {
        try { fs.Remove(path); }
        catch (SerialFsException ex) when (ex.Symbol == "NOSUCH") { }
    }

    private static void DeleteTree(SerialFs fs, string path, SdImage image)
    {
        try { fs.Remove(path); return; }
        catch (SerialFsException ex) when (ex.Symbol == "NOSUCH") { return; }
        catch (SerialFsException ex) when (ex.Symbol == "NOTEMPTY") { }
        CleanDirectory(fs, path, image, everything: true);
        fs.Remove(path);
    }

    /// <summary>Lists repeatedly until a listing is complete, deleting everything that isn't in the image.</summary>
    private static void CleanDirectory(SerialFs fs, string dir, SdImage image, bool everything = false)
    {
        for (int pass = 0; ; pass++)
        {
            fs.InvalidateCache();
            var (entries, truncated) = fs.ListWithFlag(dir);
            int removed = 0;
            foreach (var entry in entries)
            {
                if (entry.Name is "." or ".." || entry.Name.Length == 0) { continue; }
                string path = dir.Length == 0 ? entry.Name : dir + "/" + entry.Name;
                if (IsProtected(path)) { continue; }
                bool keep = !everything && (entry.IsDirectory ? image.Directories.ContainsKey(path) : image.Files.ContainsKey(path));
                if (keep) { continue; }
                if (entry.IsDirectory) { DeleteTree(fs, path, image); } else { RemoveIfPresent(fs, path); }
                removed++;
            }
            if (!truncated) { return; }
            if (removed == 0 || pass > 1000) { throw new IOException("A folder on the RT4K SD card has too many files to clean up: " + (dir.Length == 0 ? "the card" : dir)); }
        }
    }
}
