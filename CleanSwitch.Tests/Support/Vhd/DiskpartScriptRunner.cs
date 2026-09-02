using System.Diagnostics;
using System.Text;

namespace CleanSwitch.Tests.Support.Vhd;

internal sealed record DiskpartRunResult(int ExitCode, string Output, string Script);

internal static class DiskpartScriptRunner
{
    public static DiskpartRunResult Run(IEnumerable<string> lines)
    {
        var script = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"cleanswitch-vhd-diskpart-{Guid.NewGuid():N}.txt");

        File.WriteAllText(scriptPath, script, Encoding.ASCII);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add(scriptPath);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("diskpart.exe failed to start.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw new TimeoutException("diskpart.exe exceeded 120 seconds.");
            }

            var output = (stdout + Environment.NewLine + stderr).Trim();
            var result = new DiskpartRunResult(process.ExitCode, output, script);
            if (IsBenignAlreadyOnline(output))
            {
                return result;
            }

            if (process.ExitCode != 0 || LooksLikeFailure(output))
            {
                throw new InvalidOperationException(
                    "diskpart failed." +
                    Environment.NewLine + script +
                    Environment.NewLine + output);
            }

            return result;
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool IsBenignAlreadyOnline(string output) =>
        output.Contains("This disk is already online", StringComparison.OrdinalIgnoreCase) &&
        !output.Contains("is not a valid", StringComparison.OrdinalIgnoreCase) &&
        !output.Contains("The disk management services could not complete", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeFailure(string output)
    {
        return output.Contains("The disk management services could not complete", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("is not a valid", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Virtual Disk Service error", StringComparison.OrdinalIgnoreCase);
    }
}
