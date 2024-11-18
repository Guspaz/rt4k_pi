namespace rt4k_pi;

using System.Text;

public class Logger : TextWriter
{
    public record struct LogEntry(string Entry, ConsoleColor Color);

    private const int QUEUE_SIZE = 16 * 1024;
    private int logSize = 0;

    public Queue<LogEntry> Log { get; } = new();
    private readonly TextWriter oldOut = Console.Out;
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char[] buffer, int index, int count)
    {
        // Store the old color and reset it, we'll use ANSI codes for the actual output
        var oldColor = Console.ForegroundColor;
        Console.ResetColor();

        // Prepare our log entry
        var entryText = new string(buffer, index, count);
        ConsoleColor entryColor = oldColor;

        if (entryText.StartsWith("info: "))
        {
            // Special case, dim ASP.NET log stuff
            entryColor = ConsoleColor.DarkGray;
            oldOut.Write("\x1B[39m\x1B[2m"); // Default color, dim
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

        // Write to the original console
        oldOut.Write(buffer, index, count);

        // Append to our debug log
        Log.Enqueue(new(entryText, entryColor));
        logSize += count;

        // Keep the queue under the max size
        while (logSize > QUEUE_SIZE)
        {
            logSize -= Log.Dequeue().Entry.Length;
        }

        // Get the console back to how it was before
        oldOut.Write("\x1B[0m"); // Reset
        Console.ForegroundColor = oldColor;
    }
}