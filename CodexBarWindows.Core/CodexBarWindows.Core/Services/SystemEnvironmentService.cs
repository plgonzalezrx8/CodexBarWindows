using CodexBarWindows.Abstractions;

namespace CodexBarWindows.Services;

public sealed class SystemEnvironmentService : IEnvironmentService
{
    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    public string? GetEnvironmentVariable(string variable) => Environment.GetEnvironmentVariable(variable);
}
