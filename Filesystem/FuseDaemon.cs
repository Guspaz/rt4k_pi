using FuseDotNet;

namespace rt4k_pi.Filesystem
{
    public class FuseDaemon
    {
        public FuseStatus Status { get; private set; } = FuseStatus.Starting;

        public enum FuseStatus
        {
            Error,
            Installing,
            Starting,
            Running,
            Stopped
        }

        public FuseDaemon()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.WriteLine("Initializing FUSE file system");

                    Status = FuseStatus.Installing;

                    if (!Program.Installer.EnsureKsmbdInstalled())
                    {
                        Console.WriteLine("Skipping FUSE initialization since ksmbd isn't installed");
                        Status = FuseStatus.Error;
                        return;
                    }

                    Status = FuseStatus.Starting;

                    // SerialFsOperation will try to unmount the folder, so we need to stop ksmbd first
                    Util.RunCommand("systemctl", "stop ksmbd");

                    var fuseOp = new SerialFsOperations();
                    
                    // We'd ordinarily need to restart ksmbd after doing this, but ksmbd is conveniently stopped right now
                    if (!Program.Installer.EnsureKsmbdConfig())
                    {
                        Console.WriteLine("Skipping FUSE initialization since ksmbd configuration failed");
                        Status = FuseStatus.Error;
                        return;
                    }

                    Util.RunCommand("systemctl", "start ksmbd");

                    // TODO: -s runs it single-threaded. Might need this for real serial file i/o, but investigate this in the future.
                    fuseOp.Mount(["rt4k_pi", "-s", "-d", "serialfs", "-o", "nodev,nosuid,noatime,allow_other"], new FuseDotNet.Logging.ConsoleLogger());
                    Console.WriteLine("FUSE exited");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FUSE Error ({(ex is PosixException pex ? (int)pex.NativeErrorCode : ex.HResult)}): {ex.Message}");
                }

                Status = FuseStatus.Error;
            });
        }

        public void MarkAsRunning() => Status = FuseStatus.Running;
    }
}
