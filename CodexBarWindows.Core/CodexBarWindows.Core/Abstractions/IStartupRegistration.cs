namespace CodexBarWindows.Abstractions;

public interface IStartupRegistration
{
    void SetRunAtStartup(bool enable, string appName, string? executablePath = null);
}
