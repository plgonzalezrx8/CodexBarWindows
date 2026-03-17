using System.IO;
using CodexBarWindows.Abstractions;
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
