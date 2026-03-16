using Microsoft.Extensions.Hosting;
using CodexBarWindows.Models;
using CodexBarWindows.ViewModels;

namespace CodexBarWindows.Services;

public class RefreshLoopService : BackgroundService
{
    private readonly IEnumerable<IProviderProbe> _providers;
    private readonly SettingsService _settingsService;
    private readonly TrayIconViewModel _trayViewModel;
    private readonly IconGeneratorService _iconGenerator;

    public RefreshLoopService(
        IEnumerable<IProviderProbe> providers, 
        SettingsService settingsService,
        TrayIconViewModel trayViewModel,
        IconGeneratorService iconGenerator)
    {
        _providers = providers;
        _settingsService = settingsService;
        _trayViewModel = trayViewModel;
        _iconGenerator = iconGenerator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let UI load
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshProvidersAsync(stoppingToken);
            
            var interval = TimeSpan.FromMinutes(_settingsService.CurrentSettings.RefreshIntervalMinutes);
            
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshProvidersAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            if (_settingsService.CurrentSettings.EnabledProviders.TryGetValue(provider.ProviderId, out bool isEnabled) && isEnabled)
            {
                try
                {
                    var status = await provider.FetchStatusAsync(cancellationToken);
                    UpdateTrayIcon(status);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error fetching {provider.ProviderName}: {ex.Message}");
                    UpdateTrayIcon(new ProviderUsageStatus { IsError = true, ErrorMessage = ex.Message, TooltipText = $"{provider.ProviderName}: Error" });
                }
            }
        }
    }

    private void UpdateTrayIcon(ProviderUsageStatus status)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var icon = _iconGenerator.GenerateMeterIcon(status.SessionProgress, status.WeeklyProgress, status.IsError);
            
            // In a real app we'd update the specific taskbar icon for this provider, 
            // but for now we update the main one.
            var taskbarIcon = (Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)System.Windows.Application.Current.FindResource("NotifyIcon");
            if (taskbarIcon != null)
            {
                taskbarIcon.Icon = icon;
                _trayViewModel.TooltipText = status.TooltipText;
            }
        });
    }
}
