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
        var result = await _refreshCoordinator.RefreshAsync(_providers, cancellationToken);
        _historyService.RecordSnapshot(result.Statuses.ToList());
        _notificationService.ProcessStatuses(result.Statuses);
        _trayPresenter.Present(result);
    }
}
