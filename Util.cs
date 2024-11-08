using System.Diagnostics;

namespace rt4k_pi
{
    public class Util
    {
        public static string RunCommand(string FileName, string Arguments = "")
        {
            var process = new Process
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

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string error = process.StandardError.ReadToEnd();
                throw new Exception($"Error running {FileName} {Arguments} - {error}");
            }

            return output.Trim();
        }
    }
}
