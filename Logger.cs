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

    // Console.ForegroundColor is process-global, so a caller that sets it and then writes is
    // racing every other writer: WriteCore resets the colour while it runs, and any write that
    // lands in that window reads back the default and loses its colour. Callers that know their
    // colour publish it here instead, where only the writing thread can see it.
    [ThreadStatic]
    private static ConsoleColor? threadColor;

    // Tracks whether the console is sitting mid-line and who put it there, so text the device
    // never terminated can't run into whatever gets logged next.
    private bool atLineStart = true;
    private ConsoleColor lastColor = ConsoleColor.Green;

    /// <summary>Writes text in an explicit colour, immune to what other threads are doing.</summary>
    public static void Write(string text, ConsoleColor color)
    {
        threadColor = color;

        try
        {
            Console.Write(text);
        }
        finally
        {
            threadColor = null;
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

    private void WriteCore(char[] buffer, int index, int count)
    {
        // An explicit per-thread colour wins; otherwise fall back to the global one for callers
        // that still set it. Resetting the global here is safe only because anything that cares
        // about its colour has already captured it in threadColor.
        var oldColor = Console.ForegroundColor;
        Console.ResetColor();

        // Prepare our log entry
        var entryText = new string(buffer, index, count);
        ConsoleColor entryColor = threadColor ?? oldColor;
        bool isVerboseLog = entryText.StartsWith("info: ");

        // Everything the app prints is mirrored into the raw log, including the ASP.NET noise
        // that gets dropped below. That log is on disk and survives a restart, so during a crash
        // loop it's the only record of what the run was doing when it died.
        RawLog.WriteFragment(entryText);

        // Anything we're about to drop must not affect line tracking, or a suppressed entry
        // would leave us believing the console is mid-line when it isn't.
        if (!isVerboseLog || Program.Settings.VerboseLogging)
        {
            // The RT4K doesn't always terminate its last line, notably when it drops out
            // mid-reply on power off. Whoever writes next would otherwise be appended to that
            // dangling text, producing runs like "Serial Remote: pwrstatus". A change of colour
            // means a change of source, and two sources never belong on one line.
            if (!atLineStart && entryColor != lastColor)
            {
                oldOut.Write("\x1B[0m");
                oldOut.Write(Environment.NewLine);
                Log.Enqueue(new(Environment.NewLine, lastColor));
                logSize += Environment.NewLine.Length;
            }

            lastColor = entryColor;
            atLineStart = entryText.EndsWith('\n');
        }

        if (isVerboseLog)
        {
            if (Program.Settings.VerboseLogging)
            {
                // Special case, dim ASP.NET log stuff
                entryColor = ConsoleColor.DarkGray;
                oldOut.Write("\x1B[39m\x1B[2m"); // Default color, dim
            }
        }
        else
        {
            // Note: if we use any other colours in the app later, we'll need to add them here, and in DebugLog.cshtml
            oldOut.Write(entryColor switch
            {
                ConsoleColor.Green => "\x1B[32m", // Green
                ConsoleColor.DarkRed => "\x1B[31m", // Red
                _ => "\x1B[39m" // Default color
            });
        }

        if (!isVerboseLog || Program.Settings.VerboseLogging)
        {
            // Write to the original console
            oldOut.Write(buffer, index, count);

            // Get the console back to how it was before
            oldOut.Write("\x1B[0m"); // Reset

            // Append to our debug log
            Log.Enqueue(new(entryText, entryColor));
            logSize += count;
        }

        // Keep the queue under the max size
        while (logSize > QUEUE_SIZE)
        {
            logSize -= Log.Dequeue().Entry.Length;
        }

        // Get the console back to how it was before
        Console.ForegroundColor = oldColor;
    }
}