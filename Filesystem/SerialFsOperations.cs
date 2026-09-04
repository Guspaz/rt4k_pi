namespace rt4k_pi.Filesystem;

using FuseDotNet;
using FuseDotNet.Extensions;
using LTRData.Extensions.Native.Memory;

// FUSE driver that maps the RT4K's SD card onto the local filesystem over serial, so ksmbd can
// re-export it as an SMB share. Everything it does goes through SerialFs, which speaks the
// device's file-system plane (see PROTOCOL.md).
//
// The protocol has no ranged write and no open/close: a file is downloaded with "get" and
// replaced wholesale with "put". Writes are therefore staged in RAM for the lifetime of the
// open handle and uploaded when the last one closes.

internal class SerialFsOperations : IFuseOperations
{
    // Writes are buffered whole, and the Pi Zero 2 W only has 512 MB. Well clear of the largest
    // thing anyone should be writing here (a ~32 MB firmware image), but not unbounded.
    private const long MaxFileSize = 64 * 1024 * 1024;

    /// <summary>Where the card gets mounted, and what ksmbd shares out.</summary>
    private static readonly string MountPoint = Path.Combine(Directory.GetCurrentDirectory(), "serialfs");

    // A stale mount that won't come apart shouldn't hold up startup for long
    private const int UnmountTimeoutMs = 5000;

    private sealed class OpenFile(string devicePath)
    {
        public string DevicePath { get; set; } = devicePath;

        /// <summary>
        /// Guards this file's buffers and flags.
        ///
        /// openLock only covers the dictionary: two threads can hold the same OpenFile and race
        /// on its contents, and the dangerous pair is Write resizing Buffer while another Write
        /// or a flush is reading it. Holding this across a transfer also collapses the case where
        /// several threads open the same file at once into one download instead of N.
        ///
        /// Always taken after openLock, never before, so the two can't deadlock.
        /// </summary>
        public Lock Gate { get; } = new();

        /// <summary>The staged contents, materialised on the first write (or truncate).</summary>
        public byte[]? Buffer { get; set; }

        public bool Dirty { get; set; }

        /// <summary>
        /// The file's contents pulled down whole for reading, so a sequential read isn't one
        /// serial session per kernel-sized chunk. Distinct from <see cref="Buffer"/>: that one
        /// is pending changes on their way to the card, this is a read-through cache that is
        /// never uploaded.
        /// </summary>
        public byte[]? ReadCache { get; set; }

        /// <summary>Open handles referring to this file; the last one out uploads it.</summary>
        public int Handles { get; set; }
    }

    private SerialFs? fs;

    private readonly Lock openLock = new();
    private readonly Dictionary<string, OpenFile> openFiles = new(StringComparer.OrdinalIgnoreCase);

    // Resolved lazily: the FUSE daemon is started before Program.Serial exists, and the device
    // can be unplugged at any point afterwards.
    private SerialFs Fs
    {
        get
        {
            if (Program.Serial == null)
            {
                throw new SerialFsException("fs", 4, "FAIL");
            }

            return fs ??= new SerialFs(Program.Serial);
        }
    }

    public SerialFsOperations()
    {
        Console.WriteLine("Doing setup for FUSE");

        // Order matters: a mount left over from a previous run has to come off before the
        // directory can be inspected, and CreateDirectory on a live-but-dead FUSE mount blocks
        Unmount();

        Directory.CreateDirectory(MountPoint);
    }

    ~SerialFsOperations()
    {
        Dispose();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Console.WriteLine("Shutting down FUSE");
        Unmount();
    }

    /// <summary>
    /// Tears the mount down from outside the FUSE loop. Unmounting is what makes libfuse's
    /// blocking session return, so this is how the process gets to exit on a signal.
    /// </summary>
    internal static void Shutdown() => Unmount(shuttingDown: true);

    /// <summary>
    /// Clears out a mount left behind by a previous run. Nothing is mounted on a fresh boot, and
    /// unmounting something that isn't mounted is how this used to hang on startup, so the
    /// mount table is checked first and the unmount itself is given a short leash.
    /// </summary>
    private static void Unmount(bool shuttingDown = false)
    {
        if (!IsMounted())
        {
            return;
        }

        if (shuttingDown)
        {
            // On the way out, go straight for the lazy detach. It returns immediately and always
            // succeeds, which is what releases libfuse's loop. The tidier "stop ksmbd, then
            // umount -f" dance is a systemctl round trip we can't afford here: systemd is already
            // stopping our unit, and a stop job issued from inside a stop job can sit until the
            // timeout, which is the SIGKILL we're trying to avoid in the first place.
            try
            {
                Util.RunElevated($"umount -l {MountPoint}", UnmountTimeoutMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to umount serialfs on shutdown: {ex.Message}");
            }

            return;
        }

        // ksmbd holds the share's directory open, which is enough to make a plain umount fail
        // with EBUSY and send us down the lazy path on every restart
        try { Util.RunElevated("systemctl stop ksmbd"); } catch { }

        try
        {
            Util.RunElevated($"umount -f {MountPoint}", UnmountTimeoutMs);
        }
        catch
        {
            try
            {
                // Lazy detach: the old FUSE daemon is gone, so nothing is going to answer the
                // in-flight requests that are keeping the mount busy
                Util.RunElevated($"umount -l {MountPoint}", UnmountTimeoutMs);
            }
            catch
            {
                Console.WriteLine("Failed to umount serialfs");
            }
        }

        // A lazy unmount detaches the mount but doesn't necessarily complete by the time it
        // returns, and libfuse refuses to mount over a mount point that's still occupied
        for (int waited = 0; waited < UnmountTimeoutMs && IsMounted(); waited += 100)
        {
            Thread.Sleep(100);
        }

        if (IsMounted())
        {
            Console.WriteLine("serialfs is still mounted, the FUSE mount will likely fail");
        }
    }

    /// <summary>True when something is mounted on our mount point right now.</summary>
    private static bool IsMounted()
    {
        try
        {
            // Mount points with spaces in them are escaped in here, and ours could be if the
            // app was installed somewhere unusual
            return File.Exists("/proc/self/mounts")
                && File.ReadLines("/proc/self/mounts")
                    .Select(line => line.Split(' '))
                    .Any(fields => fields.Length > 1 && fields[1].Replace(@"\040", " ") == MountPoint);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the mount table: {ex.Message}");
            return false;
        }
    }

    public PosixResult StatFs(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseVfsStat statvfs)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: StatFs({path})");

        statvfs = default;

        try
        {
            var (total, free) = Fs.DiskFree();

            // The device reports KiB, so that's the block size we report the counts in
            statvfs.f_bsize = 1024;
            statvfs.f_frsize = 1024;
            statvfs.f_blocks = total;
            statvfs.f_bfree = free;
            statvfs.f_bavail = free;
            statvfs.f_namemax = SerialFs.MaxNameLength;

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("StatFs", path, ex);
        }
    }

    public void Init(ref FuseConnInfo fuse_conn_info)
    {
        Console.WriteLine($"Initializing FUSE file system, driver capabilities: {fuse_conn_info.capable}, requested: {fuse_conn_info.want}");
        Program.FuseDaemon?.MarkAsRunning();
    }

    // Functions that we don't support and just want to ignore

    /// <remarks>FAT has no timestamps we can set over serial, so this is accepted and dropped.</remarks>
    public PosixResult UTime(ReadOnlyNativeMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fileInfo)
    {
        Log($"FUSE: UTime({FuseHelper.GetString(fileNamePtr)})");
        return PosixResult.Success;
    }

    public PosixResult IoCtl(ReadOnlyNativeMemory<byte> fileNamePtr, int cmd, nint arg, ref FuseFileInfo fileInfo, FuseIoctlFlags flags, nint data)
    {
        Log($"FUSE: IoCtl({FuseHelper.GetString(fileNamePtr)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }
    public PosixResult Link(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
    {
        Log($"FUSE: Link({FuseHelper.GetString(from)}, {FuseHelper.GetString(to)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public PosixResult SymLink(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
    {
        Log($"FUSE: SymLink({FuseHelper.GetString(from)}, {FuseHelper.GetString(to)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public PosixResult ReadLink(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> target)
    {
        Log($"FUSE: ReadLink({FuseHelper.GetString(fileNamePtr)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public PosixResult ReleaseDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        Log($"FUSE: ReleaseDir({FuseHelper.GetString(fileNamePtr)})");
        return PosixResult.Success;
    }

    /// <remarks>FAT has no ownership or permissions, so these are accepted and dropped: failing
    /// them would make an SMB client abort a copy that otherwise went through fine.</remarks>
    public PosixResult ChMod(NativeMemory<byte> fileNamePtr, PosixFileMode mode)
    {
        Log($"FUSE: ChMod({FuseHelper.GetString(fileNamePtr)})");
        return PosixResult.Success;
    }

    public PosixResult ChOwn(NativeMemory<byte> fileNamePtr, int uid, int gid)
    {
        Log($"FUSE: ChOwn({FuseHelper.GetString(fileNamePtr)})");
        return PosixResult.Success;
    }

    public PosixResult FAllocate(NativeMemory<byte> fileNamePtr, FuseAllocateMode mode, long offset, long length, ref FuseFileInfo fileInfo)
    {
        Log($"FUSE: FAllocate({FuseHelper.GetString(fileNamePtr)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public PosixResult FSyncDir(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo)
    {
        Log($"FUSE: FSyncDir({FuseHelper.GetString(fileNamePtr)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    /// <summary>
    /// Uploads any staged writes.
    ///
    /// The protocol has no partial write: every flush sends the whole file. SMB flushes more
    /// than once during a copy, so honouring each one re-uploads everything written so far and
    /// makes the transfer quadratic. Release always flushes, so deferring here costs nothing in
    /// durability that the copy itself doesn't already assume.
    /// </summary>
    public PosixResult Flush(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Flush({path})");

        return PosixResult.Success;
    }

    /// <remarks>
    /// Unlike <see cref="Flush"/> this is an explicit fsync, so the caller is asking for the
    /// data to be on the card before it returns and it's worth the whole-file upload.
    /// </remarks>
    public PosixResult FSync(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: FSync({path})");

        try
        {
            FlushFile(SerialFs.DevicePath(path));
            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("FSync", path, ex);
        }
    }

    public PosixResult Access(ReadOnlyNativeMemory<byte> fileNamePtr, PosixAccessMode mask)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Access({path})");

        try
        {
            string device = SerialFs.DevicePath(path);

            // Everything on the card is readable and writable by everyone, so existence is
            // the only thing worth checking here
            return Fs.Stat(device) != null || HasStaged(device) ? PosixResult.Success : PosixResult.ENOENT;
        }
        catch (Exception ex)
        {
            return Fail("Access", path, ex);
        }
    }

    public PosixResult Create(ReadOnlyNativeMemory<byte> fileNamePtr, int mode, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Create({path})");

        try
        {
            string device = SerialFs.DevicePath(path);

            if (Fs.Stat(device) is SerialFsEntry existing && existing.IsDirectory)
            {
                return PosixResult.EISDIR;
            }

            // Don't touch the card yet. The file is staged in memory and uploaded on release,
            // which is the only upload that matters: writing an empty file here doubles the
            // number of put sessions per file, and a put is a full command/handshake/frame/
            // rename round trip regardless of payload, so for the small files a bulk copy is
            // mostly made of it was the larger half of the cost.
            //
            // Nothing observes the gap: GetAttr, Open and Read all consult the staged buffer
            // before the card, so a stat between create and the first write is still answered.
            OpenFile file = Acquire(device);
            file.Buffer = [];
            file.Dirty = true;
            fileInfo.Context = file;

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("Create", path, ex);
        }
    }

    public PosixResult GetAttr(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: GetAttr({path})");

        stat = default;

        try
        {
            string device = SerialFs.DevicePath(path);
            SerialFsEntry? entry = Fs.Stat(device);
            OpenFile? stagedFile = StagedFile(device);

            // Length is read under the gate: a concurrent write replaces Buffer with a larger
            // array, and reporting a size from the old one understates the file.
            int? stagedLength = null;

            if (stagedFile != null)
            {
                lock (stagedFile.Gate)
                {
                    stagedLength = stagedFile.Buffer?.Length;
                }
            }

            if (entry == null)
            {
                // A file that has been created but whose upload hasn't happened yet
                if (stagedLength is int length)
                {
                    stat = Describe(new SerialFsEntry(device, IsDirectory: false, length, 0), length);
                    return PosixResult.Success;
                }

                return PosixResult.ENOENT;
            }

            // Staged writes are the truth until they're uploaded, so report their size
            stat = Describe(entry, stagedLength ?? entry.Size);

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("GetAttr", path, ex);
        }
    }

    public PosixResult MkDir(ReadOnlyNativeMemory<byte> fileNamePtr, PosixFileMode mode)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: MkDir({path})");

        try
        {
            Fs.MakeDirectory(SerialFs.DevicePath(path));
            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("MkDir", path, ex);
        }
    }

    public PosixResult Open(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Open({path})");

        try
        {
            string device = SerialFs.DevicePath(path);
            SerialFsEntry? entry = Fs.Stat(device);

            if (entry == null && !HasStaged(device))
            {
                return PosixResult.ENOENT;
            }

            if (entry?.IsDirectory == true)
            {
                return PosixResult.EISDIR;
            }

            OpenFile file = Acquire(device);

            // O_TRUNC means the old contents are irrelevant, so skip downloading them
            if (fileInfo.flags.HasFlag(PosixOpenFlags.Truncate))
            {
                file.Buffer = [];
                file.Dirty = true;
            }

            fileInfo.Context = file;

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("Open", path, ex);
        }
    }

    public PosixResult OpenDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: OpenDir({path})");

        try
        {
            SerialFsEntry? entry = Fs.Stat(SerialFs.DevicePath(path));

            return entry == null ? PosixResult.ENOENT : entry.IsDirectory ? PosixResult.Success : PosixResult.ENOTDIR;
        }
        catch (Exception ex)
        {
            return Fail("OpenDir", path, ex);
        }
    }

    public PosixResult Read(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> buffer, long position, out int readLength, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Read({path}, {position}, {buffer.Length})");

        readLength = 0;

        try
        {
            string device = SerialFs.DevicePath(path);

            // Anything staged in RAM is newer than what's on the card, so it wins
            if (StagedFile(device) is OpenFile stagedFile)
            {
                lock (stagedFile.Gate)
                {
                    if (stagedFile.Buffer is byte[] staged && position < staged.Length)
                    {
                        int available = (int)Math.Min(staged.Length - position, buffer.Length);
                        staged.AsSpan((int)position, available).CopyTo(buffer.Span);
                        readLength = available;
                    }
                }

                return PosixResult.Success;
            }

            SerialFsEntry? entry = Fs.Stat(device);

            if (entry == null)
            {
                return PosixResult.ENOENT;
            }

            if (entry.IsDirectory)
            {
                return PosixResult.EISDIR;
            }

            // Reads arrive in kernel-sized chunks (128 KB at best), and each one would otherwise
            // be its own "get" session: command, handshake, frames, teardown. Over a serial link
            // that per-session cost dominates, so a few megabytes becomes tens of round trips
            // and minutes of wall time, with the session lock held throughout.
            //
            // Pulling the file down once and serving the rest of the reads from memory turns
            // that back into a single transfer. A sequential whole-file read is what a copy does,
            // so this is the case worth optimising for; Release drops the buffer again.
            OpenFile handle = fileInfo.Context as OpenFile ?? Acquire(device);

            lock (handle.Gate)
            {
                if (handle.ReadCache == null && entry.Size <= MaxFileSize)
                {
                    handle.ReadCache = entry.Size == 0 ? [] : Fs.Read(device, 0, entry.Size);
                }

                if (handle.ReadCache is byte[] cached)
                {
                    if (position < cached.Length)
                    {
                        int available = (int)Math.Min(cached.Length - position, buffer.Length);
                        cached.AsSpan((int)position, available).CopyTo(buffer.Span);
                        readLength = available;
                    }

                    return PosixResult.Success;
                }
            }

            // The device errors out on a range that runs past the end, so clamp it ourselves
            long length = Math.Min(buffer.Length, entry.Size - position);

            if (length <= 0)
            {
                return PosixResult.Success;
            }

            byte[] data = Fs.Read(device, position, length);
            data.CopyTo(buffer.Span);
            readLength = data.Length;

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("Read", path, ex);
        }
    }

    public PosixResult ReadDir(ReadOnlyNativeMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries, ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: ReadDir({path})");

        entries = [];

        try
        {
            // Materialised here rather than left lazy so a serial error surfaces as a result
            // code instead of an exception thrown back through the native callback
            List<FuseDirEntry> listing = [.. Fs.List(SerialFs.DevicePath(path))
                .Select(entry => new FuseDirEntry(entry.Name, 0, 0, Describe(entry, entry.Size)))];

            entries = FuseHelper.DotEntries.Concat(listing);

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("ReadDir", path, ex);
        }
    }

    public PosixResult Release(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Release({path})");

        try
        {
            string device = SerialFs.DevicePath(path);
            fileInfo.Context = null;

            FlushFile(device);

            lock (openLock)
            {
                if (openFiles.TryGetValue(device, out OpenFile? file) && --file.Handles <= 0)
                {
                    openFiles.Remove(device);
                }
            }

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("Release", path, ex);
        }
    }

    public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
    {
        var fromPath = FuseHelper.GetString(from);
        var toPath = FuseHelper.GetString(to);
        Log($"FUSE: Rename({fromPath}, {toPath})");

        try
        {
            string fromDevice = SerialFs.DevicePath(fromPath);
            string toDevice = SerialFs.DevicePath(toPath);

            // Staged writes are keyed by path, so flush them before the name moves out from
            // under them (a rename of an open file is exactly what an SMB save does)
            FlushFile(fromDevice);

            Fs.Rename(fromDevice, toDevice);

            lock (openLock)
            {
                if (openFiles.Remove(fromDevice, out OpenFile? file))
                {
                    file.DevicePath = toDevice;
                    openFiles[toDevice] = file;
                }
            }

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("Rename", $"{fromPath} -> {toPath}", ex);
        }
    }

    public PosixResult RmDir(ReadOnlyNativeMemory<byte> fileNamePtr)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: RmDir({path})");

        try
        {
            // "rm" removes empty directories too; a populated one comes back as NOTEMPTY
            Fs.Remove(SerialFs.DevicePath(path));
            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("RmDir", path, ex);
        }
    }

    public PosixResult Truncate(ReadOnlyNativeMemory<byte> fileNamePtr, long size)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Truncate({path}, {size})");

        try
        {
            if (size > MaxFileSize)
            {
                return PosixResult.EFBIG;
            }

            string device = SerialFs.DevicePath(path);
            OpenFile? file;

            lock (openLock)
            {
                openFiles.TryGetValue(device, out file);
            }

            // Truncating a file nobody has open still has to go out to the card, so stage it
            // temporarily and upload it on the spot.
            file ??= new OpenFile(device);

            lock (file.Gate)
            {
                byte[] buffer = LoadBuffer(file);
                Array.Resize(ref buffer, (int)size);
                file.Buffer = buffer;
                file.Dirty = true;

                if (file.Handles == 0)
                {
                    Fs.Write(device, buffer);
                    file.Dirty = false;
                }
            }

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("Truncate", path, ex);
        }
    }

    public PosixResult Unlink(ReadOnlyNativeMemory<byte> fileNamePtr)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Unlink({path})");

        try
        {
            string device = SerialFs.DevicePath(path);

            lock (openLock)
            {
                // Whatever was staged is gone with the file, so don't upload it afterwards
                if (openFiles.TryGetValue(device, out OpenFile? file))
                {
                    file.Dirty = false;
                }
            }

            Fs.Remove(device);

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("Unlink", path, ex);
        }
    }

    public PosixResult Write(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> buffer, long position, out int writtenLength, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Log($"FUSE: Write({path}, {position}, {buffer.Length})");

        writtenLength = 0;

        try
        {
            long end = position + buffer.Length;

            if (end > MaxFileSize)
            {
                return PosixResult.EFBIG;
            }

            string device = SerialFs.DevicePath(path);
            OpenFile file = fileInfo.Context as OpenFile ?? Acquire(device);

            lock (file.Gate)
            {
                byte[] staged = LoadBuffer(file);

                if (end > staged.Length)
                {
                    Array.Resize(ref staged, (int)end);
                    file.Buffer = staged;
                }

                buffer.Span.CopyTo(staged.AsSpan((int)position));
                file.Dirty = true;
                writtenLength = buffer.Length;
            }

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            return Fail("Write", path, ex);
        }
    }

    /// <summary>Takes a reference on the staging entry for a path, creating it if needed.</summary>
    private OpenFile Acquire(string devicePath)
    {
        lock (openLock)
        {
            if (!openFiles.TryGetValue(devicePath, out OpenFile? file))
            {
                file = new OpenFile(devicePath);
                openFiles[devicePath] = file;
            }

            file.Handles++;

            return file;
        }
    }

    /// <summary>
    /// The staged contents for a path, or null when nothing is being written to it.
    ///
    /// Returns the live array by reference, so callers that index into it must hold the file's
    /// gate: a concurrent write can replace it with a larger copy at any moment.
    /// </summary>
    private OpenFile? StagedFile(string devicePath)
    {
        lock (openLock)
        {
            return openFiles.TryGetValue(devicePath, out OpenFile? file) && file.Buffer != null ? file : null;
        }
    }

    /// <summary>Whether a path has staged contents, for existence checks that don't read them.</summary>
    private bool HasStaged(string devicePath) => StagedFile(devicePath) != null;

    /// <summary>
    /// Materialises the staging buffer, pulling the file's current contents down the first time
    /// so that a partial write doesn't discard the rest of it.
    /// </summary>
    /// <remarks>Callers must hold the file's gate: this both reads and assigns Buffer.</remarks>
    private byte[] LoadBuffer(OpenFile file)
    {
        if (file.Buffer != null)
        {
            return file.Buffer;
        }

        SerialFsEntry? entry = Fs.Stat(file.DevicePath);

        file.Buffer = entry == null || entry.Size == 0 ? [] : Fs.Read(file.DevicePath, 0, entry.Size);

        return file.Buffer;
    }

    /// <summary>Uploads a staged file if it has unwritten changes.</summary>
    private void FlushFile(string devicePath)
    {
        OpenFile? file;

        lock (openLock)
        {
            openFiles.TryGetValue(devicePath, out file);
        }

        if (file == null)
        {
            return;
        }

        // Held across the upload so a write can't resize Buffer out from under it, and so two
        // threads closing the same file can't both decide it's dirty and upload it twice.
        lock (file.Gate)
        {
            if (!file.Dirty)
            {
                return;
            }

            Fs.Write(devicePath, file.Buffer ?? []);
            file.Dirty = false;
        }
    }

    /// <summary>Fills in the stat structure ksmbd wants for one entry.</summary>
    private static FuseFileStat Describe(SerialFsEntry entry, long size)
    {
        var stat = new FuseFileStat
        {
            st_size = entry.IsDirectory ? 0 : size,

            // Required for ksmbd: 2 for a directory (itself plus "."), 1 for a file
            st_nlink = entry.IsDirectory ? 2 : 1,
            st_mode = (entry.IsDirectory ? PosixFileMode.Directory : PosixFileMode.Regular)
                | PosixFileMode.OwnerAll | PosixFileMode.GroupAll | PosixFileMode.OthersAll
        };

        // mt=0 is the device's sentinel for "no or invalid FAT timestamp". Leaving the fields
        // zeroed isn't a way to say "unknown" though: zero is a real time, so the client shows
        // 1970-01-01 (or 1969-12-31 west of UTC). The device has no clock and stamps nothing on
        // mkdir, so freshly created directories always land here.
        //
        // The substitute has to be constant. Using the current time would change the reported
        // mtime on every stat, and an mtime that keeps moving tells the SMB client the file was
        // modified behind its back, so it re-reads what it already had.
        TimeSpec modified = entry.Modified != 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)entry.Modified)
            : UnknownTime;

        stat.st_mtim = modified;
        stat.st_atim = modified;
        stat.st_ctim = modified;
        stat.st_birthtim = modified;

        return stat;
    }

    /// <summary>
    /// Stands in for entries the device reports no timestamp for. Fixed at startup rather than
    /// being a literal so the files don't all claim a date that looks deliberately fake, and
    /// fixed rather than live so repeated stats agree with each other.
    /// </summary>
    private static readonly DateTimeOffset UnknownTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// Logs one operation. A single directory browse over SMB is dozens of calls, so this goes
    /// to the raw log rather than the console: routing this volume through the debug log stalls
    /// it and drops entries.
    /// </summary>
    private static void Log(string message) => RawLog.Write(message);

    /// <summary>Turns an exception from the serial layer into the errno FUSE expects.</summary>
    private static PosixResult Fail(string operation, string path, Exception ex)
    {
        PosixResult result = ex is SerialFsException fsError ? fsError.Result : PosixResult.EIO;

        // Failures are rare and always worth seeing, unlike the operations themselves
        Console.WriteLine($"FUSE: {operation}({path}) = {result} ({ex.Message})");

        return result;
    }
}
