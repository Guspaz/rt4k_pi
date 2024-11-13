using FuseDotNet;

namespace rt4k_pi
{
    internal class SerialFsOperations : IFuseOperations
    {
        public void Dispose() => Console.WriteLine("Disposing FUSE file system");

        public PosixResult StatFs(ReadOnlyFuseMemory<byte> fileNamePtr, out FuseVfsStat statvfs)
        {
            // TODO: Implement me if we can get some of this data from the RT4K
            statvfs = default;
            return PosixResult.Success;
        }

        public void Init(ref FuseConnInfo fuse_conn_info) => Console.WriteLine($"Initializing FUSE file system, driver capabilities: {fuse_conn_info.capable}, requested: {fuse_conn_info.want}");

        // Functions that we don't support and just want to ignore
        public PosixResult UTime(ReadOnlyFuseMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fileInfo) => PosixResult.Success;
        public PosixResult IoCtl(ReadOnlyFuseMemory<byte> fileNamePtr, int cmd, nint arg, ref FuseFileInfo fileInfo, FuseIoctlFlags flags, nint data) => PosixResult.ENOSYS;
        public PosixResult Link(ReadOnlyFuseMemory<byte> from, ReadOnlyFuseMemory<byte> to) => PosixResult.ENOSYS;
        public PosixResult SymLink(ReadOnlyFuseMemory<byte> from, ReadOnlyFuseMemory<byte> to) => PosixResult.ENOSYS;
        public PosixResult ReadLink(ReadOnlyFuseMemory<byte> fileNamePtr, FuseMemory<byte> target) => PosixResult.ENOSYS;
        public PosixResult ReleaseDir(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.Success;

        // TODO: We might want to have these trigger a serial flush?
        public PosixResult Flush(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.ENOSYS;
        public PosixResult FSync(ReadOnlyFuseMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => PosixResult.ENOSYS;
        public PosixResult FSyncDir(ReadOnlyFuseMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => PosixResult.ENOSYS;

        // TODO: Implement these
        public PosixResult Access(ReadOnlyFuseMemory<byte> fileNamePtr, PosixAccessMode mask) => throw new NotImplementedException();
        public PosixResult Create(ReadOnlyFuseMemory<byte> fileNamePtr, PosixFileMode mode, ref FuseFileInfo fileInfo) => throw new NotImplementedException();
        public PosixResult GetAttr(ReadOnlyFuseMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo) => throw new NotImplementedException();
        public PosixResult MkDir(ReadOnlyFuseMemory<byte> fileNamePtr, PosixFileMode mode) => throw new NotImplementedException();
        public PosixResult Open(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => throw new NotImplementedException();
        public PosixResult OpenDir(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => throw new NotImplementedException();
        public PosixResult Read(ReadOnlyFuseMemory<byte> fileNamePtr, FuseMemory<byte> buffer, long position, out int readLength, ref FuseFileInfo fileInfo) => throw new NotImplementedException();
        public PosixResult ReadDir(ReadOnlyFuseMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries, ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags) => throw new NotImplementedException();
        public PosixResult Release(ReadOnlyFuseMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => throw new NotImplementedException();
        public PosixResult Rename(ReadOnlyFuseMemory<byte> from, ReadOnlyFuseMemory<byte> to) => throw new NotImplementedException();
        public PosixResult RmDir(ReadOnlyFuseMemory<byte> fileNamePtr) => throw new NotImplementedException();
        public PosixResult Truncate(ReadOnlyFuseMemory<byte> fileNamePtr, long size) => throw new NotImplementedException();
        public PosixResult Unlink(ReadOnlyFuseMemory<byte> fileNamePtr) => throw new NotImplementedException();
        public PosixResult Write(ReadOnlyFuseMemory<byte> fileNamePtr, ReadOnlyFuseMemory<byte> buffer, long position, out int writtenLength, ref FuseFileInfo fileInfo) => throw new NotImplementedException();
    }
}
