using System.Diagnostics;
using System.Text;

namespace CodexBarWindows.Services;

public class CliExecutionHelper
{
    public async Task<(int ExitCode, string Output, string Error)> ExecuteCommandAsync(string command, string arguments, int timeoutMilliseconds = 10000)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeoutMilliseconds);
            await process.WaitForExitAsync(cts.Token);

            return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { /* Ignore kill exceptions */ }
            return (-1, string.Empty, "Command timed out");
        }
        catch (Exception ex)
        {
            return (-2, string.Empty, ex.Message);
        }
    }
}
