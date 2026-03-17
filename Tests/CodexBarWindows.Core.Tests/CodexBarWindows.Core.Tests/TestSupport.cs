using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

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
