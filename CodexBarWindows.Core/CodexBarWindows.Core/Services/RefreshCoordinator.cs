using System.Collections.Concurrent;
using CodexBarWindows.Models;

namespace CodexBarWindows.Services;

public sealed record RefreshResult(IReadOnlyList<ProviderUsageStatus> Statuses, string TooltipText);

public class RefreshCoordinator
{
    private readonly SettingsService _settingsService;
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _skipCycles = new(StringComparer.OrdinalIgnoreCase);

    public RefreshCoordinator(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<RefreshResult> RefreshAsync(IEnumerable<IProviderProbe> providers, CancellationToken cancellationToken)
    {
        var fetchTasks = new List<Task<ProviderUsageStatus>>();

        foreach (var provider in providers)
        {
            if (!IsProviderEnabled(provider))
            {
                continue;
            }

            if (_skipCycles.TryGetValue(provider.ProviderId, out var skipsLeft) && skipsLeft > 0)
            {
                _skipCycles.AddOrUpdate(provider.ProviderId, 0, static (_, current) => Math.Max(0, current - 1));
                fetchTasks.Add(Task.FromResult(new ProviderUsageStatus
                {
                    ProviderId = provider.ProviderId,
                    ProviderName = provider.ProviderName,
                    IsError = true,
                    ErrorMessage = "Paused due to consecutive errors. Re-checking soon...",
                    TooltipText = $"{provider.ProviderName}: Paused (Errors)"
                }));
                continue;
            }

            fetchTasks.Add(FetchProviderSafeAsync(provider, cancellationToken));
        }

        var statuses = fetchTasks.Count == 0
            ? Array.Empty<ProviderUsageStatus>()
            : await Task.WhenAll(fetchTasks);

        return new RefreshResult(statuses, BuildTooltip(statuses));
    }

    private bool IsProviderEnabled(IProviderProbe provider)
    {
        return _settingsService.CurrentSettings.EnabledProviders.TryGetValue(provider.ProviderId, out var isEnabled)
            ? isEnabled
            : provider.IsEnabled;
    }

    private async Task<ProviderUsageStatus> FetchProviderSafeAsync(IProviderProbe provider, CancellationToken cancellationToken)
    {
        try
        {
            var status = await provider.FetchStatusAsync(cancellationToken);
            if (status.IsError)
            {
                HandleFailure(provider.ProviderId);
            }
            else
            {
                _consecutiveFailures[provider.ProviderId] = 0;
                _skipCycles[provider.ProviderId] = 0;
            }

            return status;
        }
        catch (Exception ex)
        {
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
        var failures = _consecutiveFailures.AddOrUpdate(providerId, 1, static (_, current) => current + 1);
        _skipCycles[providerId] = Math.Min(10, failures);
    }

    internal static string BuildTooltip(IReadOnlyList<ProviderUsageStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return "CodexBar (No providers enabled)";
        }

        if (statuses.Count == 1)
        {
            return statuses[0].TooltipText;
        }

        var activeCount = statuses.Count(status => !status.IsError);
        return $"CodexBar ({activeCount} active)";
    }
}
