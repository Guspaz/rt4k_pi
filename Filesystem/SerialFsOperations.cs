namespace rt4k_pi.Filesystem;

using System.Text;
using FuseDotNet;
using FuseDotNet.Extensions;

internal class SerialFsOperations : IFuseOperations
{
    private readonly string[] fakeFolders = ["/", "/foo", "/bar"];
    private readonly string[] fakeFiles = ["/foo/foo.bar", "/foo/foo.baz", "/bar/bar.foo", "/bar/bar.qux"];
    private readonly byte[] fakeContent = Encoding.ASCII.GetBytes("Hello, World!\n");

    public SerialFsOperations()
    {
        Console.WriteLine("Doing setup for FUSE");
        Util.RunCommand("ln", "-sf /usr/lib/aarch64-linux-gnu/libfuse3.so.3 libfuse3.so");

        try
        {
            // TODO: May need to stop samba before doing this?
            Util.RunCommand("umount", "-f serialfs");
        }
        catch
        {
            try
            {
                // TODO: Is this safe? Maybe reboot?
                Util.RunCommand("umount", "-l serialfs");
            }
            catch
            {
                Console.WriteLine("Failed to umount serialfs");
            }
        }
            
        
        Util.RunCommand("mkdir", "-p serialfs");
    }

    ~SerialFsOperations()
    {
        Dispose();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Console.WriteLine("Shutting down FUSE");
        try { Util.RunCommand("umount", "-f serialfs"); } catch { }
    }

    public PosixResult StatFs(ReadOnlyFuseMemory<byte> fileNamePtr, out FuseVfsStat statvfs)
    {
        Console.WriteLine($"FUSE: StatFs({FuseHelper.GetString(fileNamePtr)})");
        // TODO: Implement me if we can get some of this data from the RT4K
        statvfs = default;
        return PosixResult.Success;
    }

    public void Init(ref FuseConnInfo fuse_conn_info)
    {
        Console.WriteLine($"Initializing FUSE file system, driver capabilities: {fuse_conn_info.capable}, requested: {fuse_conn_info.want}");
        Program.FuseDaemon?.MarkAsRunning();
    }

    // Functions that we don't support and just want to ignore
    public PosixResult UTime(ReadOnlyFuseMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: UTime({FuseHelper.GetString(fileNamePtr)})");
        return PosixResult.Success;
    }

    public PosixResult IoCtl(ReadOnlyFuseMemory<byte> fileNamePtr, int cmd, nint arg, ref FuseFileInfo fileInfo, FuseIoctlFlags flags, nint data)
    {
        Console.WriteLine($"FUSE: IoCtl({FuseHelper.GetString(fileNamePtr)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }
    public PosixResult Link(ReadOnlyFuseMemory<byte> from, ReadOnlyFuseMemory<byte> to)
    {
        Console.WriteLine($"FUSE: Link({FuseHelper.GetString(from)}, {FuseHelper.GetString(to)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public PosixResult SymLink(ReadOnlyFuseMemory<byte> from, ReadOnlyFuseMemory<byte> to)
    {
        Console.WriteLine($"FUSE: SymLink({FuseHelper.GetString(from)}, {FuseHelper.GetString(to)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public PosixResult ReadLink(ReadOnlyFuseMemory<byte> fileNamePtr, FuseMemory<byte> target)
    {
        Console.WriteLine($"FUSE: ReadLink({FuseHelper.GetString(fileNamePtr)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public PosixResult ReleaseDir(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: ReleaseDir({FuseHelper.GetString(fileNamePtr)})");
        return PosixResult.Success;
    }

    // TODO: We might want to have these trigger a serial flush?
    public PosixResult Flush(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: Flush({FuseHelper.GetString(fileNamePtr)})");
        return PosixResult.Success;
    }

    public PosixResult FSync(ReadOnlyFuseMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: FSync({FuseHelper.GetString(fileNamePtr)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public PosixResult FSyncDir(ReadOnlyFuseMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: FSyncDir({FuseHelper.GetString(fileNamePtr)}) = ENOSYS");
        return PosixResult.ENOSYS;
    }

    public bool FileExists(string path) => fakeFiles.Contains(path);
    public bool DirectoryExists(string path) => fakeFolders.Contains(path);

    // TODO: Implement these
    public PosixResult Access(ReadOnlyFuseMemory<byte> fileNamePtr, PosixAccessMode mask)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Console.WriteLine($"FUSE: Access({path})");

        return FileExists(path) || DirectoryExists(path) ? PosixResult.Success : PosixResult.ENOENT;
    }

    public PosixResult Create(ReadOnlyFuseMemory<byte> fileNamePtr, PosixFileMode mode, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: Create({FuseHelper.GetString(fileNamePtr)})");
        // TODO: Support writeable file system
        return PosixResult.ENOSYS;
    }

    public PosixResult GetAttr(ReadOnlyFuseMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Console.WriteLine($"FUSE: GetAttr({path})");

        if (FileExists(path))
        {
            stat = default;
            stat.st_size = fakeContent.Length;
            stat.st_mode = PosixFileMode.Regular | PosixFileMode.OwnerAll | PosixFileMode.GroupAll | PosixFileMode.OthersAll;
            stat.st_nlink = 1; // Required for Samba, but should this have some sort of dynamic value?

            Console.WriteLine($"FUSE: GetAttr({path}) = FileExists true");
            return PosixResult.Success;
        }
        else if (DirectoryExists(path))
        {
            stat = default;
            stat.st_mode = PosixFileMode.Directory | PosixFileMode.OwnerAll | PosixFileMode.GroupAll | PosixFileMode.OthersAll;
            stat.st_nlink = 2; // Required for Samba, but should this have some sort of dynamic value?

            Console.WriteLine($"FUSE: GetAttr({path}) = DirectoryExists true");
            return PosixResult.Success;
        }
        else
        {
            stat = default;
            Console.WriteLine($"FUSE: GetAttr({path}) = Does not exist");
            return PosixResult.ENOENT;
        }
    }

    public PosixResult MkDir(ReadOnlyFuseMemory<byte> fileNamePtr, PosixFileMode mode)
    {
        Console.WriteLine($"FUSE: MkDir({FuseHelper.GetString(fileNamePtr)})");
        // TODO: Support writeable file system
        return PosixResult.ENOSYS;
    }

    public PosixResult Open(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: Open({FuseHelper.GetString(fileNamePtr)})");
        // Meant to open the file, maybe track this somehow? fileInfo.Context can store stuff
        return FileExists(FuseHelper.GetString(fileNamePtr)) ? PosixResult.Success : PosixResult.ENOENT;
    }

    public PosixResult OpenDir(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: OpenDir({FuseHelper.GetString(fileNamePtr)})");
        return DirectoryExists(FuseHelper.GetString(fileNamePtr)) ? PosixResult.Success : PosixResult.ENOENT;
    }
    
    public PosixResult Read(ReadOnlyFuseMemory<byte> fileNamePtr, FuseMemory<byte> buffer, long position, out int readLength, ref FuseFileInfo fileInfo)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Console.WriteLine($"FUSE: Read({path})");

        if (!FileExists(path))
        {
            readLength = 0;
            return PosixResult.ENOENT;
        }

        fakeContent.CopyTo(buffer.Span);
        readLength = buffer.Length;

        return PosixResult.Success;
    }

    public PosixResult ReadDir(ReadOnlyFuseMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries, ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags)
    {
        var path = FuseHelper.GetString(fileNamePtr);
        Console.WriteLine($"FUSE: ReadDir({path})");

        var files = fakeFiles.Where(s => path != "/" && s.StartsWith(path))
            .Select(entry => new FuseDirEntry(Path.GetFileName(entry), 0, 0, new() { st_mode = PosixFileMode.Regular }));

        var dirs = fakeFolders.Where(s => s != path && s.StartsWith(path))
            .Select(entry => new FuseDirEntry(Path.GetFileName(entry), 0, 0, new() { st_mode = PosixFileMode.Directory }));

        entries = FuseHelper.DotEntries.Concat(files).Concat(dirs);

        return PosixResult.Success;
    }

    public PosixResult Release(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: Release({FuseHelper.GetString(fileNamePtr)})");
        // TODO: Meant to close a file
        return PosixResult.Success;
    }

    public PosixResult Rename(ReadOnlyFuseMemory<byte> from, ReadOnlyFuseMemory<byte> to)
    {
        Console.WriteLine($"FUSE: Rename({FuseHelper.GetString(from)}, {FuseHelper.GetString(to)})");
        // TODO: support writeable file system
        return PosixResult.ENOSYS;
    }

    public PosixResult RmDir(ReadOnlyFuseMemory<byte> fileNamePtr)
    {
        Console.WriteLine($"FUSE: RmDir({FuseHelper.GetString(fileNamePtr)})");
        // TODO: support writeable file system
        return PosixResult.ENOSYS;
    }

    public PosixResult Truncate(ReadOnlyFuseMemory<byte> fileNamePtr, long size)
    {
        Console.WriteLine($"FUSE: Truncate({FuseHelper.GetString(fileNamePtr)})");
        // TODO: support writeable file system
        return PosixResult.ENOSYS;
    }

    public PosixResult Unlink(ReadOnlyFuseMemory<byte> fileNamePtr)
    {
        Console.WriteLine($"FUSE: Unlink({FuseHelper.GetString(fileNamePtr)})");
        // TODO: support writeable file system
        return PosixResult.ENOSYS;
    }

    public PosixResult Write(ReadOnlyFuseMemory<byte> fileNamePtr, ReadOnlyFuseMemory<byte> buffer, long position, out int writtenLength, ref FuseFileInfo fileInfo)
    {
        Console.WriteLine($"FUSE: Write({FuseHelper.GetString(fileNamePtr)})");
        // TODO: support writeable file system
        writtenLength = 0;
        return PosixResult.ENOSYS;
    }
}
