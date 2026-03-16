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
    private readonly UsageHistoryService _historyService;
    private readonly NotificationService _notificationService;

    public RefreshLoopService(
        IEnumerable<IProviderProbe> providers, 
        SettingsService settingsService,
        TrayIconViewModel trayViewModel,
        IconGeneratorService iconGenerator,
        UsageHistoryService historyService,
        NotificationService notificationService)
    {
        _providers = providers;
        _settingsService = settingsService;
        _trayViewModel = trayViewModel;
        _iconGenerator = iconGenerator;
        _historyService = historyService;
        _notificationService = notificationService;
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

    private readonly Dictionary<string, int> _consecutiveFailures = new();
    private readonly Dictionary<string, int> _skipCycles = new();

    private async Task RefreshProvidersAsync(CancellationToken cancellationToken)
    {
        var fetchTasks = new List<Task<ProviderUsageStatus>>();

        foreach (var provider in _providers)
        {
            if (_settingsService.CurrentSettings.EnabledProviders.TryGetValue(provider.ProviderId, out bool isEnabled) && isEnabled)
            {
                // Check backoff skip gate
                if (_skipCycles.TryGetValue(provider.ProviderId, out int skipsLeft) && skipsLeft > 0)
                {
                    _skipCycles[provider.ProviderId] = skipsLeft - 1;
                    fetchTasks.Add(Task.FromResult(new ProviderUsageStatus
                    {
                        ProviderId = provider.ProviderId,
                        ProviderName = provider.ProviderName,
                        IsError = true,
                        ErrorMessage = $"Paused due to consecutive errors. Re-checking soon...",
                        TooltipText = $"{provider.ProviderName}: Paused (Errors)"
                    }));
                    continue;
                }

                fetchTasks.Add(FetchProviderSafeAsync(provider, cancellationToken));
            }
        }

        var statuses = (await Task.WhenAll(fetchTasks)).ToList();
        
        // Record for history
        _historyService.RecordSnapshot(statuses);
        
        // Check for session quota notifications
        _notificationService.ProcessStatuses(statuses);
        
        UpdateTrayIcon(statuses);
    }

    private async Task<ProviderUsageStatus> FetchProviderSafeAsync(IProviderProbe provider, CancellationToken ct)
    {
        try
        {
            var status = await provider.FetchStatusAsync(ct);
            if (status.IsError)
            {
                HandleFailure(provider.ProviderId);
            }
            else
            {
                // Reset failure counters on success
                _consecutiveFailures[provider.ProviderId] = 0;
                _skipCycles[provider.ProviderId] = 0;
            }
            return status;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching {provider.ProviderName}: {ex.Message}");
            HandleFailure(provider.ProviderId);
            return new ProviderUsageStatus 
            { 
                ProviderId = provider.ProviderId, 
                ProviderName = provider.ProviderName, 
                IsError = true, 
                ErrorMessage = ex.Message, 
                TooltipText = $"{provider.ProviderName}: Error" 
            };
        }
    }

    private void HandleFailure(string providerId)
    {
        _consecutiveFailures.TryGetValue(providerId, out int fails);
        fails++;
        _consecutiveFailures[providerId] = fails;

        // Skip the next N cycles, where N = number of failures (capped at 10 cycles)
        _skipCycles[providerId] = Math.Min(10, fails);
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
