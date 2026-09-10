namespace rt4k_pi;

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

public record FirmwareProgress(string Phase, string Message, string? Version, bool Active, bool CanCancel, bool NeedsRecovery, long Bytes, long? TotalBytes);
public record FirmwareView(FirmwareProgress Progress, string? CurrentVersion, string? Model, bool Connected);
public record FirmwareListing(FirmwareRelease[] Releases)
{
    public string MinimumSupportedVersion => FirmwareUpdater.MinimumSupportedVersion.ToString();
}

public record FirmwareJournal
{
    public string Phase { get; init; } = "Preparing";
    public string Message { get; init; } = "Preparing firmware update.";
    public string Version { get; init; } = "";
    public int Model { get; init; } = -1;
    public string OriginalVersion { get; init; } = "";
    public string Rbf { get; init; } = "";
    public string RbfHash { get; init; } = "";
    public string BinHash { get; init; } = "";
    public string? OriginalBinHash { get; init; }
    public bool RbfCreated { get; init; }
    public bool DeviceTouched { get; init; }
    public bool PublicationStarted { get; init; }
    public bool FlashStarted { get; init; }
    public bool NeedsRecovery { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FirmwareJournal))]
[JsonSerializable(typeof(FirmwareView))]
[JsonSerializable(typeof(FirmwareProgress))]
[JsonSerializable(typeof(FirmwareListing))]
public partial class FirmwareJsonContext : JsonSerializerContext
{
}

internal static partial class FirmwarePersistence
{
    public static void Save(string directory, FirmwareJournal journal)
    {
        string temporary = Path.Combine(directory, "journal.new");
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, journal, FirmwareJsonContext.Default.FirmwareJournal);
            file.Flush(true);
        }
        File.Move(temporary, Path.Combine(directory, "journal.json"), true);
        if (OperatingSystem.IsLinux())
        {
            SyncDirectory(directory);
            SyncDirectory(Path.GetDirectoryName(Path.GetFullPath(directory))!);
        }
    }

    private static void SyncDirectory(string directory)
    {
        // O_RDONLY is portable; O_DIRECTORY has different values on x64 and ARM64.
        int fd = Open(directory, 0);
        if (fd < 0) { throw SyncError("open", directory); }
        IOException? error = null;
        try
        {
            if (Fsync(fd) != 0) { error = SyncError("sync", directory); }
        }
        finally
        {
            if (Close(fd) != 0) { error ??= SyncError("close", directory); }
        }
        if (error != null) { throw error; }
    }

    private static IOException SyncError(string operation, string directory)
    {
        int error = Marshal.GetLastPInvokeError();
        return new IOException($"Could not {operation} firmware journal directory '{directory}' (OS error {error}: {new System.ComponentModel.Win32Exception(error).Message}).");
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
