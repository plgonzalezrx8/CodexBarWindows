using Microsoft.Extensions.Hosting;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;

namespace CodexBarWindows.Services;

public class RefreshLoopService : BackgroundService
{
    private readonly IEnumerable<IProviderProbe> _providers;
    private readonly SettingsService _settingsService;
    private readonly RefreshCoordinator _refreshCoordinator;
    private readonly UsageHistoryService _historyService;
    private readonly NotificationService _notificationService;
    private readonly ITrayPresenter _trayPresenter;
    private TaskCompletionSource<bool> _settingsChangedSignal = CreateSettingsChangedSignal();

    public RefreshLoopService(
        IEnumerable<IProviderProbe> providers,
        SettingsService settingsService,
        RefreshCoordinator refreshCoordinator,
        UsageHistoryService historyService,
        NotificationService notificationService,
        ITrayPresenter trayPresenter)
    {
        _providers = providers;
        _settingsService = settingsService;
        _refreshCoordinator = refreshCoordinator;
        _historyService = historyService;
        _notificationService = notificationService;
        _trayPresenter = trayPresenter;
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

    private async Task<bool> WaitForNextRefreshAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _settingsService.CurrentSettings.RefreshIntervalMinutes;
        if (intervalMinutes <= 0)
        {
            // Manual mode: pause the loop until settings change
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

    private async Task RefreshProvidersAsync(CancellationToken cancellationToken)
    {
        var result = await _refreshCoordinator.RefreshAsync(_providers, cancellationToken);
        _historyService.RecordSnapshot(result.Statuses.ToList());
        _notificationService.ProcessStatuses(result.Statuses);
        _trayPresenter.Present(result);
    }
}
