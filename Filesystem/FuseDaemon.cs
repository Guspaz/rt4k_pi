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

                    if (!Program.Installer.EnsureSambaInstalled())
                    {
                        Console.WriteLine("Skipping FUSE initialization since Samba isn't installed");
                        Status = FuseStatus.Error;
                        return;
                    }

                    Status = FuseStatus.Starting;

                    // SerialFsOperation will try to unmount the folder, so we need to stop Samba first
                    Util.RunCommand("systemctl", "stop smbd");
                    Util.RunCommand("systemctl", "stop nmbd");

                    var fuseOp = new SerialFsOperations();

                    Util.RunCommand("systemctl", "start smbd");
                    Util.RunCommand("systemctl", "start nmbd");

                    // TODO: This is running single threaded, should it? May be higher performance if not.
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
