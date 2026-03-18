using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;
using CodexBarWindows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.Http;

namespace CodexBarWindows;

public sealed record AppHostOptions(
    bool DisableBackgroundRefresh = false,
    bool DisableUpdateChecks = false,
    bool DisableHotkeys = false);

public static class AppHostFactory
{
    public static IHost Create(string[] args, AppHostOptions? options = null)
    {
        var hostOptions = options ?? new AppHostOptions();

        return Host.CreateDefaultBuilder(args)
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<IAppDataPaths, WindowsAppDataPaths>();
                services.AddSingleton<IClock, SystemClock>();
                services.AddSingleton<IEnvironmentService, SystemEnvironmentService>();
                services.AddSingleton<IStartupRegistration, RegistryStartupRegistration>();
                services.AddSingleton<INotificationSink, ToastNotificationSink>();
                services.AddSingleton<IBrowserCookieSource, BrowserCookieService>();
                services.AddSingleton<ICredentialStore, CredentialManagerService>();
                services.AddSingleton<ICommandRunner, CliExecutionHelper>();
                services.AddSingleton<ITrayPresenter, TaskbarTrayPresenter>();

                services.AddSingleton<TrayIconViewModel>();
                services.AddTransient<SettingsViewModel>();

                services.AddSingleton<SettingsService>();
                services.AddSingleton<IconGeneratorService>();
                services.AddSingleton<CliExecutionHelper>();
                services.AddSingleton<CredentialManagerService>();
                services.AddSingleton<BrowserCookieService>();
                services.AddSingleton<RefreshCoordinator>();
                services.AddSingleton<UsageHistoryService>();
                services.AddSingleton<NotificationService>();

                services.AddTransient<IProviderProbe, CodexProvider>(sp =>
                    new CodexProvider(
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<IEnvironmentService>(),
                        CreateDefaultHttpClient()));
                services.AddTransient<IProviderProbe, ClaudeProvider>(sp =>
                    new ClaudeProvider(
                        sp.GetRequiredService<ICommandRunner>(),
                        sp.GetRequiredService<IBrowserCookieSource>(),
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<ICredentialStore>(),
                        sp.GetRequiredService<IEnvironmentService>(),
                        CreateDefaultHttpClient()));
                services.AddTransient<IProviderProbe, CursorProvider>(sp =>
                    new CursorProvider(
                        sp.GetRequiredService<IBrowserCookieSource>(),
                        sp.GetRequiredService<ICredentialStore>(),
                        sp.GetRequiredService<SettingsService>(),
                        CreateDefaultHttpClient()));
                services.AddTransient<IProviderProbe, GeminiProvider>(sp =>
                    new GeminiProvider(
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<IEnvironmentService>(),
                        CreateDefaultHttpClient()));
                services.AddTransient<IProviderProbe, AntigravityProvider>(sp =>
                    new AntigravityProvider(
                        sp.GetRequiredService<ICommandRunner>(),
                        sp.GetRequiredService<SettingsService>(),
                        CreateInsecureLocalHttpClient()));
                services.AddTransient<IProviderProbe, CopilotProvider>(sp =>
                    new CopilotProvider(
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<IEnvironmentService>(),
                        CreateDefaultHttpClient()));
                services.AddTransient<IProviderProbe, OpenRouterProvider>(sp =>
                    new OpenRouterProvider(
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<IEnvironmentService>(),
                        CreateDefaultHttpClient()));
                services.AddTransient<IProviderProbe, KiroProvider>(sp =>
                    new KiroProvider(
                        sp.GetRequiredService<ICommandRunner>(),
                        sp.GetRequiredService<SettingsService>()));
                services.AddTransient<IProviderProbe, JetBrainsProvider>(sp =>
                    new JetBrainsProvider(
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<IEnvironmentService>()));
                services.AddTransient<IProviderProbe, AugmentProvider>(sp =>
                    new AugmentProvider(
                        sp.GetRequiredService<SettingsService>(),
                        sp.GetRequiredService<IBrowserCookieSource>(),
                        sp.GetRequiredService<ICredentialStore>(),
                        CreateDefaultHttpClient()));

                if (!hostOptions.DisableHotkeys)
                {
                    services.AddSingleton<GlobalHotkeyService>();
                }

                if (!hostOptions.DisableUpdateChecks)
                {
                    services.AddSingleton<UpdateService>();
                }

                if (!hostOptions.DisableBackgroundRefresh)
                {
                    services.AddHostedService<RefreshLoopService>();
                }
            })
            .Build();
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static HttpClient CreateInsecureLocalHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }
}
