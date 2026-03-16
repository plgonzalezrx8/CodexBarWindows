using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Hardcodet.Wpf.TaskbarNotification;
using CodexBarWindows.ViewModels;
using CodexBarWindows.Services;
using CodexBarWindows.Models;
using CodexBarWindows.Providers;

namespace CodexBarWindows;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private TaskbarIcon? _taskbarIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handlers for diagnostics
        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogCrash("UnhandledException", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices((context, services) =>
                {
                    // ViewModels
                    services.AddSingleton<TrayIconViewModel>();
                    services.AddTransient<SettingsViewModel>();

                    // Core Services
                    services.AddSingleton<SettingsService>();
                    services.AddSingleton<IconGeneratorService>();
                    services.AddSingleton<CliExecutionHelper>();
                    services.AddSingleton<CredentialManagerService>();
                    services.AddSingleton<BrowserCookieService>();

                    // Providers
                    services.AddTransient<IProviderProbe, CodexProvider>();
                    services.AddTransient<IProviderProbe, ClaudeProvider>();
                    services.AddTransient<IProviderProbe, CursorProvider>();
                    services.AddTransient<IProviderProbe, GeminiProvider>();
                    services.AddTransient<IProviderProbe, AntigravityProvider>();

                    // Background Services
                    services.AddHostedService<RefreshLoopService>();
                })
                .Build();

            await _host.StartAsync();

            // Initialize System Tray Icon
            _taskbarIcon = (TaskbarIcon)FindResource("NotifyIcon");

            // Generate a default icon programmatically
            var iconGenerator = _host.Services.GetRequiredService<IconGeneratorService>();
            _taskbarIcon.Icon = iconGenerator.GenerateMeterIcon(new List<ProviderUsageStatus>());

            _taskbarIcon.DataContext = _host.Services.GetRequiredService<TrayIconViewModel>();
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            MessageBox.Show($"CodexBar failed to start:\n{ex.Message}", "CodexBar Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _taskbarIcon?.Dispose();

        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexBarWindows");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "crash.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // Last resort — nothing we can do
        }
    }
}
