namespace rt4k_pi;

using System.Text;

public class Logger : TextWriter
{
    public record struct LogEntry(string Entry, ConsoleColor Color);

    private const int QUEUE_SIZE = 16 * 1024;
    private int logSize = 0;

    // Console.Out is written from the serial processing task, the status poller and ASP.NET
    // concurrently. A single Write here is several separate oldOut.Write calls plus mutation of
    // the shared queue, so without this lock two writers interleave inside one log entry and
    // produce shredded output.
    private readonly Lock writeLock = new();

    public Queue<LogEntry> Log { get; } = new();
    private readonly TextWriter oldOut = Console.Out;
    public override Encoding Encoding => Encoding.UTF8;

    // Explicit colours belong to the writing thread, not the process-global console state.
    [ThreadStatic]
    private static ConsoleColor? threadColor;

    // Tracks whether the console is sitting mid-line and who put it there, so text the device
    // never terminated can't run into whatever gets logged next.
    private bool atLineStart = true;
    private ConsoleColor lastColor = ConsoleColor.Green;

    /// <summary>Writes text in an explicit colour, immune to what other threads are doing.</summary>
    public static void Write(string text, ConsoleColor color)
    {
        ConsoleColor? previous = threadColor;
        threadColor = color;

        try
        {
            Console.Write(text);
        }
        finally
        {
            threadColor = previous;
        }
    }

    /// <summary>Takes a consistent snapshot of the log for display.</summary>
    public LogEntry[] Snapshot()
    {
        lock (writeLock)
        {
            return [.. Log];
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (writeLock)
        {
            WriteCore(buffer, index, count);
        }
    }

    public override void Write(char value) => Write([value], 0, 1);

    public override void Flush()
    {
        lock (writeLock)
        {
            oldOut.Flush();
        }
    }

    private void WriteCore(char[] buffer, int index, int count)
    {
        var entryText = new string(buffer, index, count);
        if (entryText.Length == 0)
        {
            return;
        }

        ConsoleColor entryColor = threadColor ?? Console.ForegroundColor;
        bool isVerboseLog = entryText.StartsWith("info: ");

        // Everything the app prints is mirrored into the raw log, including the ASP.NET noise
        // that gets dropped below. That log is on disk and survives a restart, so during a crash
        // loop it's the only record of what the run was doing when it died.
        RawLog.WriteFragment(entryText);

        if (isVerboseLog && !Program.Settings.VerboseLogging)
        {
            return;
        }

        if (isVerboseLog)
        {
            entryColor = ConsoleColor.DarkGray;
        }

        // Separate an unterminated device reply from the next source's message.
        if (!atLineStart && entryColor != lastColor)
        {
            AppendEntry(Environment.NewLine, lastColor);
        }

        AppendEntry(entryText, entryColor);
        lastColor = entryColor;
        atLineStart = entryText.EndsWith('\n');
        oldOut.Flush();

        // Keep the queue under the max size
        while (logSize > QUEUE_SIZE)
        {
            logSize -= Log.Dequeue().Entry.Length;
        }
    }

    private void AppendEntry(string text, ConsoleColor color)
    {
        // Preserve ANSI colours through redirected stdout so journalctl can display them too.
        oldOut.Write(color switch
        {
            ConsoleColor.Green => "\x1B[32m",
            ConsoleColor.DarkRed => "\x1B[31m",
            ConsoleColor.DarkGray => "\x1B[39m\x1B[2m",
            _ => "\x1B[39m"
        });

        oldOut.Write(text);
        oldOut.Write("\x1B[0m");

        Log.Enqueue(new(text, color));
        logSize += text.Length;
    }
}