using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexBarWindows.Services;

public class CliExecutionHelper
{
    public async Task<(int ExitCode, string Output, string Error)> ExecuteCommandAsync(string command, string arguments, int timeoutMilliseconds = 10000)
    {
        return await ExecuteCommandAsync(command, arguments, null, timeoutMilliseconds);
    }

    public async Task<(int ExitCode, string Output, string Error)> ExecuteCommandAsync(
        string command,
        string arguments,
        string? standardInput,
        int timeoutMilliseconds = 10000)
    {
        var startInfo = CreateStartInfo(command, arguments);
        startInfo.RedirectStandardInput = standardInput != null;

        using var process = new Process { StartInfo = startInfo };
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

        try
        {
            process.Start();
            if (standardInput != null)
            {
                await process.StandardInput.WriteAsync(standardInput);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeoutMilliseconds);
            await process.WaitForExitAsync(cts.Token);

            return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { /* Ignore kill exceptions */ }
            return (-1, outputBuilder.ToString(), AppendTimeoutError(errorBuilder.ToString()));
        }
        catch (Exception ex)
        {
            return (-2, string.Empty, ex.Message);
        }
    }

    public bool CommandExists(string command)
    {
        var resolvedCommand = ResolveCommandPath(command);

        if (Path.IsPathRooted(resolvedCommand))
        {
            return File.Exists(resolvedCommand);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return !string.Equals(resolvedCommand, command, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static ProcessStartInfo CreateStartInfo(string command, string arguments)
    {
        var resolvedCommand = ResolveCommandPath(command);
        var extension = Path.GetExtension(resolvedCommand);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBaseStartInfo(
                    "cmd.exe",
                    $"/d /s /c \"\"{resolvedCommand}\"{AppendArguments(arguments)}\"");
            }

            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBaseStartInfo(
                    "pwsh.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{resolvedCommand}\"{AppendArguments(arguments)}");
            }
        }

        return CreateBaseStartInfo(resolvedCommand, arguments);
    }

    private static ProcessStartInfo CreateBaseStartInfo(string fileName, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private static string ResolveCommandPath(string command)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return command;
        }

        if (Path.IsPathRooted(command) ||
            command.Contains(Path.DirectorySeparatorChar) ||
            command.Contains(Path.AltDirectorySeparatorChar) ||
            Path.HasExtension(command))
        {
            return command;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        var candidateExtensions = new[] { ".exe", ".cmd", ".bat", ".com", ".ps1" };

        var searchDirectories = new List<string>();
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            searchDirectories.AddRange(pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            searchDirectories.Add(Path.Combine(userProfile, ".local", "bin"));
            searchDirectories.Add(Path.Combine(userProfile, ".dotnet", "tools"));
        }

        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            searchDirectories.Add(Path.Combine(roamingAppData, "npm"));
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            searchDirectories.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
        }

        foreach (var pathEntry in searchDirectories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var extension in candidateExtensions)
            {
                var candidate = Path.Combine(pathEntry, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return command;
    }

    private static string AppendArguments(string arguments)
    {
        return string.IsNullOrWhiteSpace(arguments) ? string.Empty : $" {arguments}";
    }

    private static string AppendTimeoutError(string error)
    {
        return string.IsNullOrWhiteSpace(error)
            ? "Command timed out"
            : error.TrimEnd() + Environment.NewLine + "Command timed out";
    }
}
