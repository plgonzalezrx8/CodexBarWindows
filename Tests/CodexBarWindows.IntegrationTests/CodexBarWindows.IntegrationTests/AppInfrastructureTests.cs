using CodexBarWindows;
using CodexBarWindows.Services;
using CodexBarWindows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace CodexBarWindows.IntegrationTests;

public class AppInfrastructureTests
{
    [Fact]
    public void WindowsAppDataPaths_build_expected_locations()
    {
        var paths = new WindowsAppDataPaths();
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexBarWindows");

        Assert.Equal(expectedRoot, paths.AppDataDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "settings.json"), paths.SettingsFilePath);
        Assert.Equal(Path.Combine(expectedRoot, "history.json"), paths.HistoryFilePath);
        Assert.Equal(Path.Combine(expectedRoot, "crash.log"), paths.CrashLogFilePath);
    }

    [Fact]
    public void RegistryStartupRegistration_writes_and_removes_unique_values()
    {
        var registration = new RegistryStartupRegistration();
        var appName = $"CodexBarWindows.Test.{Guid.NewGuid():N}";
        var executablePath = Path.Combine(Path.GetTempPath(), $"CodexBarWindows-{Guid.NewGuid():N}.exe");
        var runKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        try
        {
            registration.SetRunAtStartup(true, appName, executablePath);

            using (var runKey = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false))
            {
                Assert.NotNull(runKey);
                Assert.Equal($"\"{executablePath}\"", runKey!.GetValue(appName));
            }

            registration.SetRunAtStartup(false, appName, executablePath);

            using (var runKey = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false))
            {
                Assert.NotNull(runKey);
                Assert.Null(runKey!.GetValue(appName));
            }
        }
        finally
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            runKey?.DeleteValue(appName, false);
        }
    }

    [Fact]
    public async Task MockProvider_cancels_and_returns_expected_shape()
    {
        var provider = new MockProvider();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.FetchStatusAsync(cancellationSource.Token));

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.InRange(status.SessionProgress, 0.0, 1.0);
        Assert.InRange(status.WeeklyProgress, 0.0, 1.0);
        Assert.StartsWith("Mock AI", status.TooltipText, StringComparison.Ordinal);
        Assert.Contains("Session:", status.TooltipText);
        Assert.Contains("Weekly:", status.TooltipText);
    }

    [Fact]
    public void AppHostFactory_registers_optional_services_and_respects_disable_flags()
    {
        using var enabledHost = AppHostFactory.Create(Array.Empty<string>());
        Assert.NotNull(enabledHost.Services.GetRequiredService<UpdateService>());
        Assert.NotNull(enabledHost.Services.GetRequiredService<GlobalHotkeyService>());
        Assert.Contains(enabledHost.Services.GetServices<IHostedService>(), service => service is RefreshLoopService);

        using var disabledHost = AppHostFactory.Create(
            Array.Empty<string>(),
            new AppHostOptions(
                DisableBackgroundRefresh: true,
                DisableUpdateChecks: true,
                DisableHotkeys: true));

        Assert.Null(disabledHost.Services.GetService<UpdateService>());
        Assert.Null(disabledHost.Services.GetService<GlobalHotkeyService>());
        Assert.DoesNotContain(disabledHost.Services.GetServices<IHostedService>(), service => service is RefreshLoopService);
    }
}
