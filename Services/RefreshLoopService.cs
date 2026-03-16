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
    private TaskCompletionSource<bool> _settingsChangedSignal = CreateSettingsChangedSignal();

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
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let UI load
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshProvidersAsync(stoppingToken);

            if (!await WaitForNextRefreshAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task RefreshProvidersAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<(IProviderProbe Provider, ProviderUsageStatus Status)>();

        foreach (var provider in _providers)
        {
            if (_settingsService.CurrentSettings.EnabledProviders.TryGetValue(provider.ProviderId, out bool isEnabled) && isEnabled)
            {
                try
                {
                    var status = await provider.FetchStatusAsync(cancellationToken);
                    statuses.Add((provider, status));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error fetching {provider.ProviderName}: {ex.Message}");
                    statuses.Add((provider, new ProviderUsageStatus
                    {
                        IsError = true,
                        ErrorMessage = ex.Message,
                        TooltipText = $"{provider.ProviderName}: Error"
                    }));
                }
            }
        }

        var selectedStatus = SelectTrayStatus(statuses);
        if (selectedStatus != null)
        {
            UpdateTrayIcon(selectedStatus);
        }
    }

    private async Task<bool> WaitForNextRefreshAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _settingsService.CurrentSettings.RefreshIntervalMinutes;
        if (intervalMinutes <= 0)
        {
            try
            {
                await WaitForSettingsChangeAsync(stoppingToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        var delayTask = Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        var settingsChangedTask = WaitForSettingsChangeAsync(stoppingToken);

        try
        {
            await Task.WhenAny(delayTask, settingsChangedTask);
            return !stoppingToken.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private Task WaitForSettingsChangeAsync(CancellationToken stoppingToken)
    {
        var signal = _settingsChangedSignal.Task;
        return signal.WaitAsync(stoppingToken);
    }

    private ProviderUsageStatus? SelectTrayStatus(IReadOnlyList<(IProviderProbe Provider, ProviderUsageStatus Status)> statuses)
    {
        if (statuses.Count == 0)
        {
            return null;
        }

        if (_settingsService.CurrentSettings.ShowMostUsedProvider)
        {
            return statuses
                .OrderByDescending(entry => Math.Max(entry.Status.SessionProgress, entry.Status.WeeklyProgress))
                .ThenBy(entry => entry.Provider.ProviderName, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Status)
                .FirstOrDefault();
        }

        var preferredProviders = _settingsService.CurrentSettings.OverviewProviders;
        foreach (var providerId in preferredProviders)
        {
            var match = statuses.FirstOrDefault(entry => string.Equals(entry.Provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (match.Provider != null)
            {
                return match.Status;
            }
        }

        return statuses[0].Status;
    }

    private void OnSettingsChanged()
    {
        var completedSignal = Interlocked.Exchange(ref _settingsChangedSignal, CreateSettingsChangedSignal());
        completedSignal.TrySetResult(true);
    }

    public override void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        base.Dispose();
    }

    private static TaskCompletionSource<bool> CreateSettingsChangedSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
