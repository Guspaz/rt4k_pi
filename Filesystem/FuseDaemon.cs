using FuseDotNet;

namespace rt4k_pi.Filesystem
{
    public class FuseDaemon
    {
        public bool FuseRunning { get; private set; } = false;

        public void StartFuse()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.WriteLine("Instantiating FUSE file system");

                    if (!Program.Installer.SambaInstalled)
                    {
                        Console.WriteLine("Skipping FUSE initialization since Samba isn't installed");
                        FuseRunning = false;
                        return;
                    }

                    Util.RunCommand("systemctl", "stop smbd");
                    Util.RunCommand("systemctl", "stop nmbd");

                    var fuseOp = new SerialFsOperations(); // Unmounts the existing folder, we need to shut down Samba.

                    Util.RunCommand("systemctl", "start smbd");
                    Util.RunCommand("systemctl", "start nmbd");

                    FuseRunning = true; // Not actually true yet, but if it fails, we'll set it to false soon enough

                    // TODO: This is running single threaded, should it? May be higher performance if not.
                    fuseOp.Mount(["rt4k_pi", "-s", "-d", "serialfs", "-o", "nodev,nosuid,noatime,allow_other"], new FuseDotNet.Logging.ConsoleLogger());
                    Console.WriteLine("FUSE exited");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FUSE Error ({(ex is PosixException pex ? (int)pex.NativeErrorCode : ex.HResult)}): {ex.Message}");
                }

                FuseRunning = false;
            });
        }
    }
}
