/*
 * TODO/ideas list
 * - Ability to change SSID/password
 * - Some sort of SVS command support
 * - Readme page
 * - Backup settings to RT4K (requires serial file IO)
 * - SD card access (requires serial file IO)
 * - RT4K automated firmware update (requires serial file IO, possibly firmware-related query and update commands)
 * - Web-based firmware renaming/management (requires serial file IO)
 * - Better mobile experience
 * - Look into generating minimal images with pi-gen-micro
 */

namespace rt4k_pi;

using System.Runtime.InteropServices;
using FuseDotNet;
using rt4k_pi.Filesystem;

public partial class Program
{
    public static readonly string VERSION = "2.0";

    public static Serial? Serial { get; private set; }
    public static RT4K? RT4K { get; private set; }
    public static Ser2net? Ser2net {get; private set; }
    public static FuseDaemon? FuseDaemon { get; private set; }
    public static StatusDaemon StatusDaemon { get; } = new();
    public static SettingsDaemon Settings { get; } = new();
    public static Installer Installer { get; } = new();

    private static readonly Logger logger = new();

    // Shutdown has to fit inside the unit's TimeoutStopSec with room to spare
    private const int ShutdownTimeoutMs = 5000;

    private static int shuttingDown;

    private static PosixSignalRegistration[]? signalRegistrations;

    static Program()
    {
        // Ensure that we have the right working directory from the start (may get defaulted to root)
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
    }

    public static void Main(string[] args)
    {
        Console.WriteLine("Starting up rt4k_pi");

        // Before anything else: this is on disk and keeps the previous run's copy, so whatever
        // takes the process down still leaves a record behind
        RawLog.Start();

        // A crash loop leaves nothing in the service log beyond the restart itself, so make sure
        // an unhandled exception writes down what it actually was before the runtime exits
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            RawLog.Write($"FATAL: unhandled exception{(e.IsTerminating ? " (terminating)" : "")}: {e.ExceptionObject}");
            Console.WriteLine($"Unhandled exception: {e.ExceptionObject}");
        };

        // Faulted tasks nobody awaited surface here rather than as a crash, so they'd otherwise
        // be invisible
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RawLog.Write($"FATAL: unobserved task exception: {e.Exception}");
        };

        // Run all output through the debug log
        Console.SetOut(logger);
        
        Console.WriteLine($"rt4k_pi v{VERSION}\n");

        // We don't actually support Windows, but it's useful for testing.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (!@args.Contains("--bypassinstaller"))
            {
                Installer.CheckInstall();
            }

            Installer.DisableWifiPowerSave();

            // libfuse's session loop doesn't return on SIGTERM, so systemd's stop would time out
            // and SIGKILL us, leaving the mount behind for the next run to trip over. Unmounting
            // is what releases the loop, so it has to happen while we can still run code.
            //
            // The registrations are held in a field on purpose: PosixSignalRegistration
            // unregisters when it's finalized, so a local would let the GC quietly undo this.
            signalRegistrations =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleShutdownSignal),
                PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleShutdownSignal)
            ];

            FuseDaemon = new();
        }

        Serial = new Serial(2000000);
        RT4K = new RT4K(Serial);
        Ser2net = new Ser2net(Serial, 2000);

        if (Settings.EnableSer2net)
        {
            Ser2net.Start();
        }

        Settings.Load();

        RunWeb();
    }

    /// <summary>
    /// Unmounts on the way out so systemd's stop doesn't have to escalate to SIGKILL, then exits.
    ///
    /// Handling the signal at all suppresses .NET's default termination, so this has to end the
    /// process itself: without that, SIGTERM is swallowed, systemd waits out TimeoutStopSec and
    /// SIGKILLs us anyway, which is worse than not handling it.
    /// </summary>
    private static void HandleShutdownSignal(PosixSignalContext context)
    {
        // Lock-free and self-contained: the previous attempt logged through the normal path and
        // produced nothing at all for a SIGTERM we know arrived, which means the thread was
        // blocked before it ever got a line out.
        RawLog.WriteUrgent($"SIGNAL: received {context.Signal} from {DescribeSignalSender()}");

        // Signal handlers run on a dedicated thread and systemd may send more than one. Only the
        // first gets to do the work; the rest return so they don't re-enter the unmount.
        if (Interlocked.Exchange(ref shuttingDown, 1) != 0)
        {
            RawLog.WriteUrgent($"SIGNAL: {context.Signal} ignored, already shutting down");
            return;
        }

        context.Cancel = true;

        // Unmounting talks to systemctl and umount, and this has to finish inside
        // TimeoutStopSec. Doing it on a worker with a hard cap means a wedged umount costs us a
        // few seconds rather than the SIGKILL it used to.
        bool unmounted = Task.Run(SerialFsOperations.Shutdown).Wait(ShutdownTimeoutMs);

        RawLog.WriteUrgent(unmounted
            ? "SIGNAL: unmount finished, exiting"
            : $"SIGNAL: unmount did not finish within {ShutdownTimeoutMs}ms, exiting anyway");

        // Exit rather than return: Environment.Exit runs finalizers and can block on whatever is
        // already stuck, so if that happens we still need to go. This is the last resort.
        Task.Run(() =>
        {
            Thread.Sleep(ShutdownTimeoutMs);
            RawLog.WriteUrgent("SIGNAL: exit did not complete, aborting the process");
            Environment.FailFast("Shutdown hung");
        });

        Environment.Exit(0);
    }

    /// <summary>
    /// Best-effort identification of whatever asked us to stop.
    ///
    /// "Deactivated successfully" in the journal only says the stop was clean, not who ordered
    /// it, and that difference is the whole question when a restart has no obvious cause. This
    /// won't name a sender that has already exited, but a live systemctl or a shell shows up
    /// outright, which distinguishes "someone ran systemctl" from "systemd stopped the unit".
    /// </summary>
    private static string DescribeSignalSender()
    {
        try
        {
            // Field 4 of /proc/self/stat is the parent pid. The comm field before it is
            // parenthesised and may itself contain spaces, so index from its final ')'.
            string self = File.ReadAllText("/proc/self/stat");
            string parent = self[(self.LastIndexOf(')') + 2)..].Split(' ')[1];

            string name = File.Exists($"/proc/{parent}/comm")
                ? File.ReadAllText($"/proc/{parent}/comm").Trim()
                : "already exited";

            return $"parent pid {parent} ({name})";
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name})";
        }
    }
}

