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

                    // The service is enabled from a previous run, so it comes up on its own at
                    // boot and shares out the bare (empty) mount point. Nothing should be able
                    // to connect to the share until the card is actually mounted behind it.
                    StopShare();

                    if (!Program.Installer.EnsureFuseInstalled())
                    {
                        Console.WriteLine("Skipping FUSE initialization since libfuse3 isn't available");
                        Status = FuseStatus.Error;
                        return;
                    }

                    if (!Program.Installer.EnsureKsmbdInstalled())
                    {
                        Console.WriteLine("Skipping FUSE initialization since ksmbd isn't installed");
                        Status = FuseStatus.Error;
                        return;
                    }

                    Status = FuseStatus.Starting;

                    var fuseOp = new SerialFsOperations();

                    // ksmbd is stopped right now, so it'll pick this up when we start it
                    if (!Program.Installer.EnsureKsmbdConfig())
                    {
                        Console.WriteLine("Skipping FUSE initialization since ksmbd configuration failed");
                        Status = FuseStatus.Error;
                        return;
                    }

                    // The share is started from Init, once the mount is actually up

                    // -f keeps libfuse in the foreground. Without it fuse_daemonize() forks and
                    // exits the parent, which under Type=simple tells systemd the service has
                    // stopped: it then SIGTERMs the surviving fork, and since fork() in .NET
                    // carries over only the calling thread, that fork has no signal handler and
                    // no thread pool left to answer with, so it sits there until the stop
                    // timeout and gets SIGKILLed. That was the restart loop.
                    // TODO: -s runs it single-threaded. Might need this for real serial file i/o, but investigate this in the future.
                    // No -d: fuse's debug mode logs a timestamped line (plus a hex dump of the
                    // data) for every operation, which buries everything else in the debug log.
                    // Operations log themselves into the raw log, see SerialFsOperations.
                    RawLog.Write("FUSE: calling Mount");
                    fuseOp.Mount(["rt4k_pi", "-f", "-s", "serialfs", "-o", "nodev,nosuid,noatime,allow_other"], new FuseDotNet.Logging.NullLogger());
                    RawLog.Write("FUSE: Mount returned");
                    Console.WriteLine("FUSE exited");
                }
                catch (Exception ex)
                {
                    RawLog.Write($"FUSE: Mount threw: {ex}");
                    Console.WriteLine($"FUSE Error ({(ex is PosixException pex ? (int)pex.NativeErrorCode : ex.HResult)}): {ex.Message}");
                }
                finally
                {
                    // Whether we failed to start or the mount went away underneath us, the share
                    // must not outlive it: an exported empty directory looks to a client exactly
                    // like an RT4K with nothing on its card.
                    StopShare();
                }

                Status = FuseStatus.Error;
            });
        }

        /// <summary>
        /// Called from the FUSE init callback, so the share only goes up once the mount is
        /// serving. Starting ksmbd takes a moment, so it doesn't happen on the callback thread.
        /// </summary>
        public void MarkAsRunning()
        {
            Status = FuseStatus.Running;

            Task.Run(() =>
            {
                try
                {
                    RawLog.Write("ksmbd: starting share");
                    Util.RunElevated("systemctl start ksmbd");
                    RawLog.Write("ksmbd: share started");
                    Console.WriteLine("SMB share started");
                }
                catch (Exception ex)
                {
                    RawLog.Write($"ksmbd: failed to start share: {ex}");
                    Console.WriteLine($"Failed to start the SMB share: {ex.Message}");
                }
            });
        }

        private static void StopShare()
        {
            try
            {
                Util.RunElevated("systemctl stop ksmbd");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to stop the SMB share: {ex.Message}");
            }
        }
    }
}
