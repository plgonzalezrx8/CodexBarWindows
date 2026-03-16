using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Codex provider — runs `codex status --json` via CLI and parses
/// session/weekly usage.  Falls back to regex parsing if the output
/// is not valid JSON (e.g. older CLI versions that render a text panel).
/// </summary>
public class CodexProvider : IProviderProbe
{
    private readonly CliExecutionHelper _cliHelper;
    private readonly SettingsService _settings;

    public string ProviderId => "codex";
    public string ProviderName => "Codex";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("codex", true);

    public CodexProvider(CliExecutionHelper cliHelper, SettingsService settings)
    {
        _cliHelper = cliHelper;
        _settings = settings;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        // Try JSON mode first (codex status --json)
        var result = await _cliHelper.ExecuteCommandAsync("codex", "status --json", 8000);

        if (result.ExitCode != 0)
        {
            // Check for common error conditions
            if (result.Error.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
                result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return MakeError("Codex CLI missing. Install via `npm i -g @openai/codex`.");
            }

            if (result.Error.Contains("update available", StringComparison.OrdinalIgnoreCase))
            {
                return MakeError("Codex CLI update needed. Run `npm update -g @openai/codex`.");
            }

            return MakeError($"CLI Error {result.ExitCode}: {result.Error}");
        }

        try
        {
            return ParseJsonOutput(result.Output);
        }
        catch
        {
            // Fall back to regex-based text parsing
            return ParseTextOutput(result.Output);
        }
    }

    // ── JSON Parsing ────────────────────────────────────────────────

    private static ProviderUsageStatus ParseJsonOutput(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;

        double sessionProgress = 0.0;
        double weeklyProgress = 0.0;
        double? credits = null;
        string? sessionReset = null;
        string? weeklyReset = null;

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

        if (root.TryGetProperty("credits", out var creditsEl))
            credits = creditsEl.GetDouble();

        if (root.TryGetProperty("sessionReset", out var srEl))
            sessionReset = srEl.GetString();

        if (root.TryGetProperty("weeklyReset", out var wrEl))
            weeklyReset = wrEl.GetString();

        var tooltipParts = new List<string> { "Codex" };
        tooltipParts.Add($"Session: {sessionProgress * 100:F1}%");
        tooltipParts.Add($"Weekly: {weeklyProgress * 100:F1}%");
        if (credits.HasValue) tooltipParts.Add($"Credits: {credits.Value:F2}");
        if (sessionReset != null) tooltipParts.Add($"Session resets: {sessionReset}");
        if (weeklyReset != null) tooltipParts.Add($"Weekly resets: {weeklyReset}");

        return new ProviderUsageStatus
        {
            ProviderId = "codex",
            ProviderName = "Codex",
            SessionProgress = sessionProgress,
            WeeklyProgress = weeklyProgress,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    // ── Regex Fallback (text panel output) ───────────────────────────

    private static ProviderUsageStatus ParseTextOutput(string text)
    {
        var clean = StripAnsiCodes(text);

        double sessionProgress = 0.0;
        double weeklyProgress = 0.0;

        // Parse "5h limit" percentage
        var fiveHMatch = Regex.Match(clean, @"5h limit[^\n]*?(\d+)%", RegexOptions.IgnoreCase);
        if (fiveHMatch.Success && int.TryParse(fiveHMatch.Groups[1].Value, out var fivePct))
            sessionProgress = (100 - fivePct) / 100.0; // "percent left" → "used"

        // Parse "Weekly limit" percentage
        var weeklyMatch = Regex.Match(clean, @"Weekly limit[^\n]*?(\d+)%", RegexOptions.IgnoreCase);
        if (weeklyMatch.Success && int.TryParse(weeklyMatch.Groups[1].Value, out var weekPct))
            weeklyProgress = (100 - weekPct) / 100.0;

        // Parse credits
        var creditsMatch = Regex.Match(clean, @"Credits:\s*([0-9][0-9.,]*)", RegexOptions.IgnoreCase);
        var creditsText = creditsMatch.Success ? $"\nCredits: {creditsMatch.Groups[1].Value}" : "";

        if (!fiveHMatch.Success && !weeklyMatch.Success && !creditsMatch.Success)
        {
            return MakeError("Could not parse Codex CLI output.");
        }

        return new ProviderUsageStatus
        {
            ProviderId = "codex",
            ProviderName = "Codex",
            SessionProgress = sessionProgress,
            WeeklyProgress = weeklyProgress,
            IsError = false,
            TooltipText = $"Codex\nSession: {sessionProgress * 100:F1}%\nWeekly: {weeklyProgress * 100:F1}%{creditsText}"
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "codex",
        ProviderName = "Codex",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Codex: {message}"
    };

    private static string StripAnsiCodes(string text) =>
        Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", "");
}
