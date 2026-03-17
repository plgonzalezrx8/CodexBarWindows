namespace CodexBarWindows.Abstractions;

public interface IEnvironmentService
{
    string GetFolderPath(Environment.SpecialFolder folder);
    string? GetEnvironmentVariable(string variable);
}
