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
        var statuses = new List<ProviderUsageStatus>();

        foreach (var provider in _providers)
        {
            if (_settingsService.CurrentSettings.EnabledProviders.TryGetValue(provider.ProviderId, out bool isEnabled) && isEnabled)
            {
                try
                {
                    var status = await provider.FetchStatusAsync(cancellationToken);
                    statuses.Add(status);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error fetching {provider.ProviderName}: {ex.Message}");
                    statuses.Add(new ProviderUsageStatus { ProviderId = provider.ProviderId, ProviderName = provider.ProviderName, IsError = true, ErrorMessage = ex.Message, TooltipText = $"{provider.ProviderName}: Error" });
                }
            }
        }

        UpdateTrayIcon(statuses);
    }

    private void UpdateTrayIcon(List<ProviderUsageStatus> statuses)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // Update ViewModel for the popup
            _trayViewModel.ProviderStatuses.Clear();
            foreach (var s in statuses)
            {
                _trayViewModel.ProviderStatuses.Add(s);
            }

            // Update Icon
            var icon = _iconGenerator.GenerateMeterIcon(statuses);
            
            var taskbarIcon = (Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)System.Windows.Application.Current.FindResource("NotifyIcon");
            if (taskbarIcon != null)
            {
                taskbarIcon.Icon = icon;
                
                // Construct combined tooltip
                if (statuses.Count == 0)
                {
                    _trayViewModel.TooltipText = "CodexBar (No providers enabled)";
                }
                else if (statuses.Count == 1)
                {
                    _trayViewModel.TooltipText = statuses[0].TooltipText;
                }
                else
                {
                    var activeCount = statuses.Count(s => !s.IsError);
                    _trayViewModel.TooltipText = $"CodexBar ({activeCount} active)";
                }
            }
        });
    }
}
