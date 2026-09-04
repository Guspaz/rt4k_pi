namespace rt4k_pi.Filesystem;

using FuseDotNet;

// Host side of the RT4K's file-system plane (see PROTOCOL.md, "File System Operations").
//
// The device exposes six text-plane commands (ls, stat, df, mv, rm, mkdir) plus the two
// bulk-transfer sessions (get/put) that Serial already implements. Everything here is
// synchronous because the FUSE callbacks that consume it are, and the device serializes
// every SD access behind a single lock anyway.

/// <summary>A single directory entry as reported by "ls"/"stat".</summary>
public record SerialFsEntry(string Name, bool IsDirectory, long Size, ulong Modified);

/// <summary>Raised when the device answers a file-system command with an error line.</summary>
public class SerialFsException(string op, int code, string symbol) : Exception($"{op} err={code} {symbol}")
{
    public int Code { get; } = code;
    public string Symbol { get; } = symbol;

    /// <summary>
    /// The closest errno for this RT_* status. Codes are documented in PROTOCOL.md's
    /// "RT_* status code table"; the symbol is authoritative, the number is not.
    /// </summary>
    public PosixResult Result => Symbol switch
    {
        "OK" => PosixResult.Success,
        "NOSUCH" => PosixResult.ENOENT,
        "PERM" => PosixResult.EACCES,
        "UNSUPPORTED" => PosixResult.ENOTSUP,
        "EXIST" => PosixResult.EEXIST,
        "INVALID_NAME" => PosixResult.EINVAL,
        "NAME_TOO_LONG" => PosixResult.ENAMETOOLONG,
        "BUSY" => PosixResult.EBUSY,
        "NO_SPACE" => PosixResult.ENOSPC,
        // POSIX allows EEXIST for a non-empty directory, and FuseDotNet has no ENOTEMPTY
        "NOTEMPTY" => PosixResult.EEXIST,
        _ => PosixResult.EIO
    };
}

public class SerialFs(Serial serial)
{
    /// <summary>Longest name FatFs will accept (FF_MAX_LFN).</summary>
    public const int MaxNameLength = 255;

    // The firmware caps a listing at SF_LS_MAX entries and cannot tell us whether more existed
    private const int ListingCap = 512;

    // "ls" walks the FAT for every entry, so it gets far longer than the 1s command default
    private const int ListTimeoutMs = 20000;
    private const int CommandTimeoutMs = 5000;

    // The SD lock is held by whichever port got there first, including our own OSD mirror
    private const int BusyRetries = 20;
    private const int BusyRetryMs = 100;

    // A cold boot takes several seconds before the file-system commands answer
    private const int WakeTimeoutMs = 20000;
    private const int WakePollMs = 1000;

    // One waker at a time: an SMB browse fans out into many concurrent operations, and they
    // would otherwise each send their own "pwr on" and toggle the unit back off
    private static readonly Lock wakeLock = new();

    // Directory listings are the only way to answer the GetAttr storm a single SMB directory
    // browse produces, so they're held briefly. Everything we change invalidates the cache.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

    private readonly Lock cacheLock = new();
    private readonly Dictionary<string, (DateTime Fetched, List<SerialFsEntry> Entries, bool Truncated)> listings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops every cached listing. Called after anything that changes the card.</summary>
    public void InvalidateCache()
    {
        lock (cacheLock)
        {
            listings.Clear();
        }
    }

    /// <summary>
    /// Drops only the listings a change to one path can affect: the directory holding it, and,
    /// if it is itself a directory, its own listing.
    ///
    /// Clearing everything instead is very expensive during a bulk copy. Each created file is a
    /// change, and the SMB client stats and re-lists as it goes, so a blanket invalidation makes
    /// every file in the batch re-run "ls" on every directory it touches. Over a serial link
    /// that listing is the dominant cost, and it is being thrown away for changes that could not
    /// have affected it.
    /// </summary>
    private void InvalidatePath(string devicePath)
    {
        int slash = devicePath.LastIndexOf('/');
        string parent = slash < 0 ? "" : devicePath[..slash];

        lock (cacheLock)
        {
            listings.Remove(parent);
            listings.Remove(devicePath);
        }
    }

    /// <summary>Turns a FUSE path ("/profile/foo.rt4") into a device path ("profile/foo.rt4").</summary>
    public static string DevicePath(string path) => path.Trim('/');

    /// <summary>The parent and file name halves of a FUSE path, both in device form.</summary>
    public static (string Parent, string Name) Split(string path)
    {
        string device = DevicePath(path);
        int slash = device.LastIndexOf('/');

        return slash < 0 ? ("", device) : (device[..slash], device[(slash + 1)..]);
    }

    /// <summary>Lists a directory. The path is in device form ("" for the card's root).</summary>
    public List<SerialFsEntry> List(string devicePath) => ListWithFlag(devicePath).Entries;

    private (List<SerialFsEntry> Entries, bool Truncated) ListWithFlag(string devicePath)
    {
        lock (cacheLock)
        {
            if (listings.TryGetValue(devicePath, out var cached) && DateTime.UtcNow - cached.Fetched < CacheLifetime)
            {
                return (cached.Entries, cached.Truncated);
            }
        }

        List<string> lines = Send("ls", devicePath.Length == 0 ? "ls" : $"ls {devicePath}",
            line => line.StartsWith("ls end") || line.StartsWith("ls err="), ListTimeoutMs);

        var entries = new List<SerialFsEntry>();
        bool truncated = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("ent "))
            {
                if (ParseEntry(line[4..]) is SerialFsEntry entry)
                {
                    entries.Add(entry);
                }
            }
            else if (line.StartsWith("ls truncated at"))
            {
                truncated = true;
            }
        }

        // "truncated" only means the cap was reached, so a directory of exactly 512 entries
        // reports it too. Either way we can't trust the listing to be complete.
        truncated = truncated || entries.Count >= ListingCap;

        lock (cacheLock)
        {
            listings[devicePath] = (DateTime.UtcNow, entries, truncated);
        }

        return (entries, truncated);
    }

    /// <summary>
    /// Attributes for one entry, or null if it doesn't exist. Answered from the parent's cached
    /// listing where possible, since a single SMB browse asks about every file in a directory.
    /// </summary>
    public SerialFsEntry? Stat(string devicePath)
    {
        if (devicePath.Length == 0)
        {
            return new SerialFsEntry("", IsDirectory: true, Size: 0, Modified: 0);
        }

        int slash = devicePath.LastIndexOf('/');
        string parent = slash < 0 ? "" : devicePath[..slash];
        string name = slash < 0 ? devicePath : devicePath[(slash + 1)..];

        try
        {
            var (entries, truncated) = ListWithFlag(parent);

            // FAT is case-insensitive, and so is the SMB client on the other end
            if (entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) is SerialFsEntry found)
            {
                return found;
            }

            // A complete listing that doesn't mention the name is proof it isn't there
            if (!truncated)
            {
                return null;
            }
        }
        catch (SerialFsException ex) when (ex.Symbol == "NOSUCH")
        {
            // Parent is missing or isn't a directory, so neither is the child
            return null;
        }

        return StatDirect(devicePath, name);
    }

    /// <summary>Asks the device directly, bypassing the parent listing.</summary>
    private SerialFsEntry? StatDirect(string devicePath, string name)
    {
        List<string> lines;

        try
        {
            lines = Send("stat", $"stat {devicePath}", line => line.StartsWith("stat ") || line.StartsWith("stat:"), CommandTimeoutMs);
        }
        catch (SerialFsException ex) when (ex.Symbol == "NOSUCH")
        {
            return null;
        }

        string? reply = lines.FirstOrDefault(line => line.StartsWith("stat t="));

        // "stat" echoes back the path the caller passed, which isn't the name we want to report
        return reply == null ? null : ParseEntry(reply[5..]) is SerialFsEntry entry ? entry with { Name = name } : null;
    }

    /// <summary>Volume size and free space, both in KiB.</summary>
    public (ulong TotalKiB, ulong FreeKiB) DiskFree()
    {
        List<string> lines = Send("df", "df", line => line.StartsWith("df ") || line.StartsWith("df:"), CommandTimeoutMs);
        string reply = lines.First(line => line.StartsWith("df total="));
        var fields = Serial.ParseFields(reply);

        ulong Field(string key) => fields.TryGetValue(key, out string? value) && ulong.TryParse(value, out ulong parsed) ? parsed : 0;

        return (Field("total"), Field("free"));
    }

    public void MakeDirectory(string devicePath)
    {
        Send("mkdir", $"mkdir {devicePath}", line => line == "mkdir ok" || line.StartsWith("mkdir err="), CommandTimeoutMs);
        InvalidatePath(devicePath);
    }

    public void Remove(string devicePath)
    {
        Send("rm", $"rm {devicePath}", line => line == "rm ok" || line.StartsWith("rm err="), CommandTimeoutMs);
        InvalidatePath(devicePath);
    }

    /// <summary>Renames or moves an entry. Old and new are separated on the wire by a literal '|'.</summary>
    public void Rename(string fromDevicePath, string toDevicePath)
    {
        Send("mv", $"mv -f {fromDevicePath}|{toDevicePath}", line => line == "mv ok" || line.StartsWith("mv err="), CommandTimeoutMs);
        InvalidatePath(fromDevicePath);
        InvalidatePath(toDevicePath);
    }

    /// <summary>Reads a byte range out of a file on the card.</summary>
    public byte[] Read(string devicePath, long offset, long length)
        => serial.GetFileAsync(devicePath, offset, length, quiet: true).GetAwaiter().GetResult();

    /// <summary>Replaces a file on the card. The device stages it and renames it into place.</summary>
    public void Write(string devicePath, byte[] data)
    {
        serial.PutFileAsync(devicePath, data, quiet: true).GetAwaiter().GetResult();
        InvalidatePath(devicePath);
    }

    /// <summary>
    /// Runs one file-system command, retrying while the device's SD lock is held and turning
    /// its "&lt;op&gt; err=&lt;code&gt; &lt;SYMBOL&gt;" line into an exception.
    /// </summary>
    private List<string> Send(string op, string command, Func<string, bool> isTerminal, int timeoutMs)
    {
        EnsureAwake();

        for (int attempt = 0; ; attempt++)
        {
            // "<op>: busy" is terminal too, but it isn't an error the caller should see yet.
            // The console echo stays off no matter what: this is the highest-volume traffic in
            // the app, and it goes to the raw log instead.
            RawLog.Write($"serial> {command}");

            List<string> lines = serial.SendCommandAsync(command, line => isTerminal(line) || line == $"{op}: busy", timeoutMs,
                echoIf: _ => false).GetAwaiter().GetResult();

            foreach (string line in lines)
            {
                RawLog.Write($"serial< {line}");
            }

            if (lines.Contains($"{op}: busy"))
            {
                if (attempt >= BusyRetries)
                {
                    throw new SerialFsException(op, 68, "BUSY");
                }

                Thread.Sleep(BusyRetryMs);
                continue;
            }

            if (lines.FirstOrDefault(line => line.StartsWith($"{op} err=")) is string error)
            {
                var fields = Serial.ParseFields(error);
                string[] parts = error.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                throw new SerialFsException(op,
                    fields.TryGetValue("err", out string? code) && int.TryParse(code, out int parsed) ? parsed : 4,
                    parts.Length > 2 ? parts[2] : "FAIL");
            }

            if (!lines.Any(isTerminal))
            {
                // No terminal line: the device is off, wedged, or the reply was lost
                throw new SerialFsException(op, 67, "TIMEOUT");
            }

            return lines;
        }
    }

    /// <summary>
    /// Wakes the device if the SD card is being accessed while it's in standby. The standby
    /// dispatcher discards every file-system command silently, so without this the share is
    /// mounted but every operation times out.
    /// </summary>
    private static void EnsureAwake()
    {
        if (!Program.Settings.WakeOnFileAccess || Program.RT4K is not RT4K rt4k)
        {
            return;
        }

        // Only act on a reading we already have: probing here would put a "status" round trip
        // in front of every single file operation.
        if (rt4k.Power != RT4K.PowerState.Off)
        {
            return;
        }

        lock (wakeLock)
        {
            // Another operation in this batch may have just done it
            if (rt4k.Power != RT4K.PowerState.Off)
            {
                return;
            }

            Console.WriteLine($"{RT4K.DisplayName} is in standby, waking it for file access");
            rt4k.PowerOn();

            // PowerOn only schedules a status refresh, so wait for the unit to actually come up
            // rather than letting the command that triggered this fail against a booting device.
            for (int waited = 0; waited < WakeTimeoutMs; waited += WakePollMs)
            {
                Thread.Sleep(WakePollMs);

                if (rt4k.RefreshPower() == RT4K.PowerState.On)
                {
                    Console.WriteLine($"{RT4K.DisplayName} is awake");
                    return;
                }
            }

            Console.WriteLine($"{RT4K.DisplayName} did not wake up, file access will likely fail");
        }
    }

    /// <summary>
    /// Parses the shared tail of "ls"/"stat" replies: "t=F sz=123 mt=456 [at=0x20] nm=&lt;name&gt;".
    /// Names may contain spaces, so everything after "nm=" is the name.
    /// </summary>
    private static SerialFsEntry? ParseEntry(string line)
    {
        int name = line.IndexOf("nm=", StringComparison.Ordinal);

        if (name < 0)
        {
            return null;
        }

        var fields = Serial.ParseFields(line[..name]);

        fields.TryGetValue("sz", out string? size);
        fields.TryGetValue("mt", out string? modified);

        return new SerialFsEntry(
            line[(name + 3)..],
            fields.TryGetValue("t", out string? type) && type == "D",
            long.TryParse(size, out long parsedSize) ? parsedSize : 0,
            ulong.TryParse(modified, out ulong parsedModified) ? parsedModified : 0);
    }
}
