namespace CodexBarWindows.Abstractions;

public interface ICommandRunner
{
    Task<CommandResult> ExecuteCommandAsync(
        string command,
        string arguments,
        string? standardInput = null,
        int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default);

    bool CommandExists(string command);
}

public readonly record struct CommandResult(int ExitCode, string Output, string Error);
