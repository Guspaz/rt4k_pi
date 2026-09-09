namespace rt4k_pi;

using System.Diagnostics;

public class Util
{
    // Long enough for anything we run that isn't apt (which passes its own), short enough that
    // a wedged command doesn't take the whole app down with it
    private const int DefaultTimeoutMs = 30000;

    // How long to keep draining a command's output after it has already exited
    private const int PipeDrainMs = 2000;

    public static string RunCommand(string FileName, string Arguments = "", int timeoutMs = DefaultTimeoutMs)
    {
        if (Program.Settings.VerboseLogging)
        {
            Console.WriteLine($"exec: {FileName} {Arguments}");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FileName,
                Arguments = Arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        // Both pipes are drained at once: a command that fills the 64 KB stderr buffer while
        // we sit blocked on stdout would deadlock with each side waiting on the other.
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();

        // WaitForExit(int) waits for the process. The parameterless overload additionally waits
        // for the redirected pipes to reach EOF, which never happens if the command left
        // something behind holding the write end of them (systemctl and fusermount both do), so
        // an already-exited command can hang the caller forever. That is exactly how a boot with
        // no RT4K attached ended up stuck with a defunct umount child, so don't use it here.
        if (!process.WaitForExit(timeoutMs))
        {
            Console.WriteLine($"Timed out after {timeoutMs}ms running {FileName} {Arguments}, killing it");

            try { process.Kill(entireProcessTree: true); } catch { }

            throw new Exception($"Timed out running {FileName} {Arguments}");
        }

        if (process.ExitCode != 0)
        {
            throw new Exception($"Error running {FileName} {Arguments} - {Drain(error)}");
        }

        return Drain(output).Trim();
    }

    /// <summary>
    /// Runs a command with root privileges.
    ///
    /// Under systemd the service already runs as root, so sudo is not only unnecessary but
    /// harmful: it may not be installed in a minimal image, and it's an extra process in the
    /// tree for no gain. When run by hand it is needed, but it must never be allowed to ask for
    /// a password. There is no terminal on a headless boot, so a prompt would sit there
    /// invisibly until the command timed out and the install step failed for no apparent
    /// reason. -n makes sudo fail immediately instead, which surfaces as a real error.
    /// </summary>
    public static string RunElevated(string arguments, int timeoutMs = DefaultTimeoutMs)
    {
        if (!IsRoot)
        {
            try
            {
                return RunCommand("sudo", $"-n {arguments}", timeoutMs);
            }
            catch (Exception ex) when (ex.Message.Contains("password is required", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(
                    $"Root privileges are required to run \"{arguments}\", but sudo asked for a password. " +
                    "Run rt4k_pi with sudo, or give this user passwordless sudo, so it can set itself up unattended.");
            }
        }

        // Already root, so run the command directly. Deliberately not via a shell: these
        // commands carry arguments we don't control the shape of (the ksmbd password among
        // them), and handing them to sh would make quoting our problem. Splitting off the
        // program name keeps the exact same argument string sudo would have passed on.
        int space = arguments.IndexOf(' ');

        return space < 0
            ? RunCommand(arguments, "", timeoutMs)
            : RunCommand(arguments[..space], arguments[(space + 1)..], timeoutMs);
    }

    /// <summary>Whether this process is already running as root, so sudo would be redundant.</summary>
    public static bool IsRoot { get; } = OperatingSystem.IsLinux() && geteuid() == 0;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    /// <summary>
    /// Collects what a finished command wrote, giving up rather than blocking if the pipe is
    /// still held open by something the command spawned.
    /// </summary>
    private static string Drain(Task<string> reader)
    {
        try
        {
            return reader.Wait(PipeDrainMs) ? reader.Result : "";
        }
        catch
        {
            return "";
        }
    }
}
