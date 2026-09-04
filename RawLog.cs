namespace rt4k_pi;

using System.Text;

/// <summary>
/// A plain, unformatted, file-backed log for high-volume diagnostic output.
///
/// The main <see cref="Logger"/> is built for the debug log page: it tracks colours and line
/// state, echoes to the console, and holds a small in-memory queue. That work is per-write and
/// serialised on a single lock, which is fine for the handful of lines a normal run produces but
/// collapses under per-FUSE-operation logging, where one SMB directory browse is thousands of
/// writes. This takes none of that: no colours, no console echo, no line tracking.
///
/// It writes to disk rather than to memory, and it starts recording before anything else runs.
/// A crash loop takes the process down every few seconds, which loses an in-memory buffer and
/// leaves no time to enable anything from the web UI, so neither can be a precondition for
/// having a log of what happened.
///
/// That volume is also why it's off by default: writing a line per FUSE operation for the life of
/// the machine is needless wear on the SD card. It's switched on by creating a marker file, which
/// persists across restarts so a crash loop can still be captured from the very first line.
/// </summary>
public static class RawLog
{
    /// <summary>The current log, and the previous run's, kept alongside the executable.</summary>
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rawlog.txt");
    private static readonly string PreviousLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rawlog.previous.txt");

    /// <summary>
    /// Shutdown and crash notes, appended to across runs. Kept separate from the rotating logs
    /// because during a restart loop this is the part that must not be overwritten, and it's
    /// small enough that never rotating it doesn't matter.
    /// </summary>
    private static readonly string UrgentLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rawlog.events.txt");

    /// <summary>
    /// Presence of this file turns logging on. It's a file rather than a setting because the
    /// thing this exists to diagnose is a crash loop, where the web UI may be unreachable and
    /// settings.json may never be read: "touch rawlog.enable" over SSH always works.
    /// </summary>
    private static readonly string EnableMarkerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rawlog.enable");

    // Bounds the damage a crash loop or a stuck operation can do to the SD card. Once the
    // current log passes this, it becomes the "previous" one and a fresh log starts, so there
    // are never more than two files and never more than ~2x this on disk.
    private const long MaxBytes = 4 * 1024 * 1024;

    private static readonly Lock writeLock = new();
    private static StreamWriter? writer;
    private static long written;

    /// <summary>
    /// Whether anything is being recorded. Off unless the marker file exists: this log writes a
    /// line per FUSE operation, and a single SMB directory browse is thousands of them, which is
    /// far too much to be writing to a cheap SD card during normal use.
    /// </summary>
    public static bool Enabled { get; private set; }

    /// <summary>
    /// Opens the log. The previous run's file is kept, which is the whole point during a restart
    /// loop: the log that matters is the one from the run that just died, not this one.
    /// </summary>
    public static void Start()
    {
        lock (writeLock)
        {
            try
            {
                Enabled = File.Exists(EnableMarkerPath);

                if (!Enabled)
                {
                    return;
                }

                Rotate();

                WriteLine($"# rt4k_pi v{Program.VERSION} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                // Logging is not worth failing to boot over
                Console.WriteLine($"Could not open the raw log: {ex.Message}");
            }
        }
    }

    /// <summary>Turns logging on or off, and remembers it across restarts.</summary>
    public static void SetEnabled(bool enabled)
    {
        lock (writeLock)
        {
            try
            {
                if (enabled)
                {
                    File.WriteAllText(EnableMarkerPath, "Delete this file to stop raw logging.\n");

                    if (!Enabled)
                    {
                        Enabled = true;
                        Rotate();
                        WriteLine($"# rt4k_pi v{Program.VERSION} logging enabled {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    }
                }
                else
                {
                    File.Delete(EnableMarkerPath);

                    Enabled = false;
                    writer?.Dispose();
                    writer = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not change raw logging: {ex.Message}");
            }
        }
    }

    /// <summary>Moves the current log aside and opens a fresh one. Caller holds the lock.</summary>
    private static void Rotate()
    {
        writer?.Dispose();
        writer = null;

        try
        {
            if (File.Exists(LogPath))
            {
                File.Move(LogPath, PreviousLogPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not rotate the raw log: {ex.Message}");
        }

        // Buffered, not AutoFlush. Flushing every line turns each FUSE operation into its own
        // synchronous write, rewriting the same flash block over and over, which is precisely
        // the access pattern that wears out a cheap card. A timer flushes instead, so a crash
        // costs at most the last second of log.
        writer = new StreamWriter(LogPath, append: false, Encoding.UTF8, bufferSize: 16 * 1024);
        written = 0;

        flushTimer ??= new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
    }

    private const int FlushIntervalMs = 1000;
    private static Timer? flushTimer;

    private static void Flush()
    {
        lock (writeLock)
        {
            try { writer?.Flush(); } catch { }
        }
    }

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        lock (writeLock)
        {
            WriteLine(message);
        }
    }

    /// <summary>
    /// Records a line without taking any lock, for use when the process may already be wedged.
    ///
    /// <see cref="Write"/> is no use in that situation: if a thread is stuck holding the lock,
    /// anything trying to report the problem blocks behind it and the log simply stops, which is
    /// exactly what a SIGTERM that produced no log entry looks like. This opens the file for
    /// itself and appends, so it can't be blocked by whatever else is going on.
    /// </summary>
    public static void WriteUrgent(string message)
    {
        try
        {
            File.AppendAllText(UrgentLogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Last-resort logging cannot be allowed to throw
        }
    }

    // Console.WriteLine reaches the logger as several separate writes (the text, then the
    // newline), and the serial reader emits partial lines as bytes arrive. Mirroring each
    // fragment as its own entry would shred the output and bury it in timestamps, so fragments
    // are assembled here and only complete lines are written.
    private static readonly StringBuilder pending = new();

    /// <summary>
    /// Mirrors console output, assembling fragments into whole lines.
    /// </summary>
    public static void WriteFragment(string text)
    {
        if (!Enabled)
        {
            return;
        }

        lock (writeLock)
        {
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    WriteLine(pending.ToString());
                    pending.Clear();
                }
                else if (c != '\r')
                {
                    pending.Append(c);
                }
            }

            // A line the device never terminated would otherwise sit here indefinitely, which
            // during a hang is exactly the line worth seeing
            if (pending.Length > 4096)
            {
                WriteLine(pending.ToString());
                pending.Clear();
            }
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private static void WriteLine(string message)
    {
        if (writer == null)
        {
            return;
        }

        try
        {
            // Timestamps are the point of this log: the interesting failures so far have been
            // about what happened in the seconds before a hang, which ordering alone can't show.
            string entry = $"{DateTime.Now:HH:mm:ss.fff} {message}";

            // AutoFlush, so a SIGKILL can't take the last few seconds of the log with it. That
            // costs write throughput, but a log that loses the end is no use for a crash.
            writer.WriteLine(entry);
            written += entry.Length + 1;

            if (written > MaxBytes)
            {
                Rotate();
            }
        }
        catch
        {
            // A full or read-only filesystem shouldn't take the app down through the logger
        }
    }

    /// <summary>
    /// Returns both logs, newest run last, ready to be saved or sent somewhere. The previous
    /// run's log comes first because after a crash that's the one holding the failure.
    /// </summary>
    public static string Dump()
    {
        StringBuilder sb = new();

        lock (writeLock)
        {
            try { writer?.Flush(); } catch { }

            Append(sb, PreviousLogPath, "previous run");
            Append(sb, LogPath, "current run");
            Append(sb, UrgentLogPath, "shutdown and crash events (all runs)");
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string path, string label)
    {
        sb.AppendLine($"##### {label}: {path}");

        try
        {
            if (File.Exists(path))
            {
                // Opened share-all: this reads the file the writer above still has open
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                sb.AppendLine(reader.ReadToEnd());
            }
            else
            {
                sb.AppendLine("(no log)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not be read: {ex.Message})");
        }

        sb.AppendLine();
    }
}
