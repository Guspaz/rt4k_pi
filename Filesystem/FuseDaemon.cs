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
                    // -s: the mount is single-threaded. Multi-threading was tried and reverted:
                    // it bought nothing measurable, because the wire is one exclusive resource.
                    // A get/put holds Serial's sessionLock for the whole payload, and every
                    // metadata command (ls/stat/mv/df) takes that same lock, so extra FUSE
                    // threads just queue up on one semaphore instead of one callback. Worse, the
                    // per-command timeouts only start once the lock is acquired, so the real wait
                    // is unbounded and the client times out first.
                    //
                    // This only becomes worth revisiting if the firmware gains partial reads and
                    // writes. Transfers could then be chunked, releasing the lock between chunks,
                    // which is what actually lets metadata be served during a copy (and would
                    // give clients real progress instead of a 0%-then-99% jump).
                    //
                    // The thread-safety work done for the multi-threaded attempt is deliberately
                    // kept: per-file state is guarded by OpenFile.Gate and the listing cache by
                    // SerialFs's cacheLock. It is harmless under -s and is a prerequisite for
                    // that future change, so don't reintroduce unguarded shared mutable state.
                    //
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
