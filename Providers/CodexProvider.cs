using System.Text.Json;
using System.Text.RegularExpressions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

public class CodexProvider : IProviderProbe
{
    private readonly CliExecutionHelper _cliHelper;

    public string ProviderId => "codex";
    public string ProviderName => "Codex";
    public bool IsEnabled => true; // Ideally checked via Settings

    public CodexProvider(CliExecutionHelper cliHelper)
    {
        _cliHelper = cliHelper;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        // Execute the codex status command (hypothetical representation)
        var result = await _cliHelper.ExecuteCommandAsync("codex", "status --json", 5000);

        if (result.ExitCode != 0)
        {
            return new ProviderUsageStatus
            {
                IsError = true,
                ErrorMessage = $"CLI Error {result.ExitCode}: {result.Error}",
                TooltipText = "Codex: Error fetching status"
            };
        }

        try
        {
            // Try to parse JSON output
            // Example JSON: { "session": { "used": 1500, "limit": 5000 }, "weekly": { "used": 10000, "limit": 50000 } }
            var root = JsonDocument.Parse(result.Output).RootElement;
            
            double sessionProgress = 0.0;
            double weeklyProgress = 0.0;

            if (root.TryGetProperty("session", out var sessionEl))
            {
                double used = sessionEl.GetProperty("used").GetDouble();
                double limit = sessionEl.GetProperty("limit").GetDouble();
                sessionProgress = limit > 0 ? used / limit : 0;
            }

            if (root.TryGetProperty("weekly", out var weeklyEl))
            {
                double used = weeklyEl.GetProperty("used").GetDouble();
                double limit = weeklyEl.GetProperty("limit").GetDouble();
                weeklyProgress = limit > 0 ? used / limit : 0;
            }

            return new ProviderUsageStatus
            {
                SessionProgress = sessionProgress,
                WeeklyProgress = weeklyProgress,
                IsError = false,
                TooltipText = $"Codex\nSession: {(sessionProgress * 100):F1}%\nWeekly: {(weeklyProgress * 100):F1}%"
            };
        }
        catch (Exception ex)
        {
            // Fallback to regex if JSON parsing fails, or return error
            return new ProviderUsageStatus
            {
                IsError = true,
                ErrorMessage = "Failed to parse Codex CLI output: " + ex.Message,
                TooltipText = "Codex: Parse Error"
            };
        }
    }
}
