using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;
using System.Net;
using System.Net.Http;

namespace CodexBarWindows.Core.Tests;

internal sealed class TestAppDataPaths : IAppDataPaths, IDisposable
{
    public TestAppDataPaths()
    {
        AppDataDirectory = Path.Combine(Path.GetTempPath(), "CodexBarWindows.Tests", Guid.NewGuid().ToString("N"));
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

internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 3, 16, 12, 0, 0, DateTimeKind.Utc);
    public DateTime LocalNow { get; set; } = new(2026, 3, 16, 8, 0, 0, DateTimeKind.Local);
}

internal sealed class FakeNotificationSink : INotificationSink
{
    public List<(string Title, string Message)> Messages { get; } = [];

    public void Show(string title, string message)
    {
        Messages.Add((title, message));
    }
}

internal sealed class FakeProvider : IProviderProbe
{
    private readonly Queue<Func<Task<ProviderUsageStatus>>> _responses = new();

    public FakeProvider(string providerId, string providerName, params Func<Task<ProviderUsageStatus>>[] responses)
    {
        ProviderId = providerId;
        ProviderName = providerName;
        foreach (var response in responses)
        {
            _responses.Enqueue(response);
        }
    }

    public string ProviderId { get; }
    public string ProviderName { get; }
    public bool IsEnabled => true;

    public Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        if (_responses.Count == 0)
        {
            return Task.FromResult(new ProviderUsageStatus
            {
                ProviderId = ProviderId,
                ProviderName = ProviderName,
                TooltipText = ProviderName
            });
        }

        return _responses.Dequeue()();
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
    public Dictionary<string, CachedCookieEntry> CachedCookies { get; } = new(StringComparer.OrdinalIgnoreCase);

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
        CachedCookies[providerId] = new CachedCookieEntry
        {
            CookieHeader = cookieHeader,
            SourceLabel = sourceLabel,
            StoredAt = DateTime.UtcNow
        };
    }

    public CachedCookieEntry? GetCachedCookieHeader(string providerId)
    {
        return CachedCookies.TryGetValue(providerId, out var entry) ? entry : null;
    }

    public void ClearCachedCookieHeader(string providerId)
    {
        CachedCookies.Remove(providerId);
    }

    private static string BuildKey(string providerId, string? accountLabel) =>
        accountLabel == null ? providerId : $"{providerId}:{accountLabel}";
}

internal sealed class FakeCommandRunner : ICommandRunner
{
    public bool Exists { get; set; }
    public List<(string Command, string Arguments, string? StandardInput, int Timeout)> Calls { get; } = [];
    public Func<string, string, string?, int, CommandResult>? Handler { get; set; }

    public Task<CommandResult> ExecuteCommandAsync(
        string command,
        string arguments,
        string? standardInput = null,
        int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((command, arguments, standardInput, timeoutMilliseconds));
        var result = Handler?.Invoke(command, arguments, standardInput, timeoutMilliseconds)
            ?? new CommandResult(0, string.Empty, string.Empty);
        return Task.FromResult(result);
    }

    public bool CommandExists(string command) => Exists;
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
