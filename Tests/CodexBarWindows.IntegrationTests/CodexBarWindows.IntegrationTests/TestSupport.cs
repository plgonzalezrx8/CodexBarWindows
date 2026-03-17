using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.IntegrationTests;

internal sealed class TestAppDataPaths : IAppDataPaths, IDisposable
{
    public TestAppDataPaths()
    {
        AppDataDirectory = Path.Combine(Path.GetTempPath(), "CodexBarWindows.IntegrationTests", Guid.NewGuid().ToString("N"));
    }

    public string AppDataDirectory { get; }
    public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");
    public string HistoryFilePath => Path.Combine(AppDataDirectory, "history.json");
    public string CrashLogFilePath => Path.Combine(AppDataDirectory, "crash.log");

    public void Dispose()
    {
        if (Directory.Exists(AppDataDirectory))
        {
            Directory.Delete(AppDataDirectory, recursive: true);
        }
    }
}

internal sealed class FakeEnvironmentService : IEnvironmentService
{
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Environment.SpecialFolder, string> Folders { get; } = new();

    public string GetFolderPath(Environment.SpecialFolder folder) => Folders[folder];

    public string? GetEnvironmentVariable(string variable) =>
        Variables.TryGetValue(variable, out var value) ? value : null;
}

internal sealed class FakeCookieSource : IBrowserCookieSource
{
    public string? CookieHeader { get; set; }

    public string? GetCookieHeader(string domain) => CookieHeader;
}

internal sealed class FakeCredentialStore : ICredentialStore
{
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SaveCredential(string providerId, string secret, string? accountLabel = null)
    {
        Secrets[BuildKey(providerId, accountLabel)] = secret;
    }

    public string? GetCredential(string providerId, string? accountLabel = null)
    {
        Secrets.TryGetValue(BuildKey(providerId, accountLabel), out var value);
        return value;
    }

    public void DeleteCredential(string providerId, string? accountLabel = null)
    {
        Secrets.Remove(BuildKey(providerId, accountLabel));
    }

    public IReadOnlyList<StoredCredentialInfo> ListAllCredentials() => [];

    public void CacheCookieHeader(string providerId, string cookieHeader, string sourceLabel)
    {
        SaveCredential(providerId + ":cookie", cookieHeader);
        SaveCredential(providerId + ":cookie-meta", $"{DateTime.UtcNow:O}|{sourceLabel}");
    }

    public CachedCookieEntry? GetCachedCookieHeader(string providerId)
    {
        var cookie = GetCredential(providerId + ":cookie");
        if (cookie == null)
        {
            return null;
        }

        return new CachedCookieEntry
        {
            CookieHeader = cookie,
            SourceLabel = "test",
            StoredAt = DateTime.UtcNow
        };
    }

    public void ClearCachedCookieHeader(string providerId)
    {
        DeleteCredential(providerId + ":cookie");
        DeleteCredential(providerId + ":cookie-meta");
    }

    private static string BuildKey(string providerId, string? accountLabel) =>
        accountLabel == null ? providerId : $"{providerId}:{accountLabel}";
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request));
    }
}
