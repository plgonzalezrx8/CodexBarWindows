using System.IO;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;
using System.Windows;
using System.Windows.Threading;

namespace CodexBarWindows.WpfSmokeTests;

internal sealed class TestAppDataPaths : IAppDataPaths, IDisposable
{
    public TestAppDataPaths()
    {
        AppDataDirectory = Path.Combine(Path.GetTempPath(), "CodexBarWindows.WpfSmokeTests", Guid.NewGuid().ToString("N"));
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

internal sealed class FakeStartupRegistration : IStartupRegistration
{
    public List<(bool Enable, string AppName, string? ExecutablePath)> Calls { get; } = [];

    public void SetRunAtStartup(bool enable, string appName, string? executablePath = null)
    {
        Calls.Add((enable, appName, executablePath));
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

internal sealed class FakeTrayPresenter : ITrayPresenter
{
    public List<RefreshResult> Results { get; } = [];

    public void Present(RefreshResult result)
    {
        Results.Add(result);
    }
}

internal sealed class FakeProviderProbe : IProviderProbe
{
    private readonly Queue<Func<Task<ProviderUsageStatus>>> _responses = new();

    public FakeProviderProbe(string providerId, string providerName, params Func<Task<ProviderUsageStatus>>[] responses)
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

internal static class StaTestRunner
{
    public static Task RunAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                action().GetAwaiter().GetResult();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static async Task RunAsync(Action action)
    {
        await RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    public static Application EnsureApplication()
    {
        return Application.Current ?? new Application();
    }
}
