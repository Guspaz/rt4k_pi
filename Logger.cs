using System.Text;

namespace rt4k_pi
{
    public class Logger : TextWriter
    {
        public struct LogEntry { public string Entry; public ConsoleColor Color; }
        private const int QUEUE_SIZE = 16 * 1024;
        private int logSize = 0;

        public Queue<LogEntry> Log { get; } = new Queue<LogEntry>();

        private readonly TextWriter oldOut = Console.Out;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char[] buffer, int index, int count)
        {
            // Write to the original console
            oldOut.Write(buffer, index, count);

            logSize += count;

            // Append to our debug log
            Log.Enqueue(
                new LogEntry()
                {
                    Entry = new string(buffer[index..count]),
                    Color = Console.ForegroundColor
                });

            // Keep the queue under the max size
            while (logSize > QUEUE_SIZE)
            {
                logSize -= Log.Dequeue().Entry.Length;
            }
        }
    }
}
