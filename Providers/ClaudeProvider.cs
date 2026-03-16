using System.Text.RegularExpressions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Claude provider — runs `claude` CLI with `/usage` and `/status` subcommands,
/// parses the text output for session/weekly/opus percentages.
///
/// Auth priority:
///   1. CLI PTY (claude /usage)
///   2. OAuth token from Credential Manager
///   3. Browser cookies (for Claude web dashboard)
/// </summary>
public class ClaudeProvider : IProviderProbe
{
    private readonly CliExecutionHelper _cliHelper;
    private readonly SettingsService _settings;
    private readonly CredentialManagerService _credentialManager;

    public string ProviderId => "claude";
    public string ProviderName => "Claude";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("claude", true);

    public ClaudeProvider(
        CliExecutionHelper cliHelper,
        SettingsService settings,
        CredentialManagerService credentialManager)
    {
        _cliHelper = cliHelper;
        _settings = settings;
        _credentialManager = credentialManager;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        // Primary approach: run `claude` CLI with /usage subcommand
        // Claude CLI uses an interactive TUI, so we run it with arguments to get text output.
        var result = await _cliHelper.ExecuteCommandAsync("claude", "/usage", 20000);

        if (result.ExitCode != 0)
        {
            if (result.Error.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
                result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return MakeError("Claude CLI is not installed or not on PATH.");
            }

            if (result.Error.Contains("token_expired", StringComparison.OrdinalIgnoreCase) ||
                result.Error.Contains("token has expired", StringComparison.OrdinalIgnoreCase))
            {
                return MakeError("Claude CLI token expired. Run `claude login` to refresh.");
            }

            if (result.Error.Contains("authentication_error", StringComparison.OrdinalIgnoreCase))
            {
                return MakeError("Claude CLI authentication error. Run `claude login`.");
            }

            return MakeError($"CLI Error {result.ExitCode}: {result.Error}");
        }

        return ParseUsageOutput(result.Output);
    }

    // ── Parsing ─────────────────────────────────────────────────────

    private static ProviderUsageStatus ParseUsageOutput(string rawText)
    {
        var clean = StripAnsiCodes(rawText);

        // Check for known error conditions
        if (clean.Contains("failed to load usage data", StringComparison.OrdinalIgnoreCase))
            return MakeError("Claude CLI could not load usage data. Open the CLI and retry `/usage`.");

        if (clean.Contains("rate_limit_error", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("rate limited", StringComparison.OrdinalIgnoreCase))
            return MakeError("Claude CLI usage endpoint is rate limited. Please try again later.");

        // Extract session percentage
        int? sessionPct = ExtractPercentNearLabel("Current session", clean);

        // Extract weekly percentage
        int? weeklyPct = ExtractPercentNearLabel("Current week (all models)", clean)
                      ?? ExtractPercentNearLabel("Current week", clean);

        // Extract opus/premium percentage
        int? opusPct = ExtractPercentNearLabel("Current week (Opus)", clean)
                    ?? ExtractPercentNearLabel("Current week (Sonnet only)", clean)
                    ?? ExtractPercentNearLabel("Current week (Sonnet)", clean);

        // Fallback: grab all percentages in order
        if (sessionPct == null)
        {
            var allPcts = AllPercents(clean);
            if (allPcts.Count > 0) sessionPct = allPcts[0];
            if (allPcts.Count > 1 && weeklyPct == null) weeklyPct = allPcts[1];
            if (allPcts.Count > 2 && opusPct == null) opusPct = allPcts[2];
        }

        if (sessionPct == null)
            return MakeError("Could not parse Claude usage data.");

        // Claude reports "percent left", so convert to "used"
        double sessionUsed = (100.0 - sessionPct.Value) / 100.0;
        double weeklyUsed = weeklyPct.HasValue ? (100.0 - weeklyPct.Value) / 100.0 : 0.0;

        // Extract reset descriptions
        var sessionReset = ExtractResetNearLabel("Current session", clean);
        var weeklyReset = ExtractResetNearLabel("Current week", clean);

        // Extract email/account
        var email = ExtractFirst(@"(?i)Account:\s+(\S+@\S+)", clean)
                 ?? ExtractFirst(@"(?i)Email:\s+(\S+@\S+)", clean);

        var tooltipParts = new List<string> { "Claude" };
        tooltipParts.Add($"Session: {sessionUsed * 100:F1}% used ({sessionPct}% left)");
        if (weeklyPct.HasValue)
            tooltipParts.Add($"Weekly: {weeklyUsed * 100:F1}% used ({weeklyPct}% left)");
        if (opusPct.HasValue)
            tooltipParts.Add($"Opus/Sonnet: {opusPct}% left");
        if (sessionReset != null)
            tooltipParts.Add(sessionReset);
        if (email != null)
            tooltipParts.Add($"Account: {email}");

        return new ProviderUsageStatus
        {
            ProviderId = "claude",
            ProviderName = "Claude",
            SessionProgress = sessionUsed,
            WeeklyProgress = weeklyUsed,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    // ── Percent Extract ─────────────────────────────────────────────

    private static int? ExtractPercentNearLabel(string label, string text)
    {
        var lines = text.Split('\n');
        var normalizedLabel = NormalizeForSearch(label);

        for (int i = 0; i < lines.Length; i++)
        {
            if (!NormalizeForSearch(lines[i]).Contains(normalizedLabel))
                continue;

            // Search the next 12 lines for a percentage
            for (int j = i; j < Math.Min(i + 12, lines.Length); j++)
            {
                var pct = PercentFromLine(lines[j]);
                if (pct.HasValue) return pct;
            }
        }
        return null;
    }

    private static int? PercentFromLine(string line)
    {
        var match = Regex.Match(line, @"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var rawVal = double.Parse(match.Groups[1].Value);
        var clamped = Math.Max(0, Math.Min(100, rawVal));

        var lower = line.ToLowerInvariant();
        if (lower.Contains("used") || lower.Contains("spent") || lower.Contains("consumed"))
            return (int)Math.Round(Math.Max(0, Math.Min(100, 100 - clamped)));
        if (lower.Contains("left") || lower.Contains("remaining") || lower.Contains("available"))
            return (int)Math.Round(clamped);

        return null; // Ambiguous without context keyword
    }

    private static List<int> AllPercents(string text)
    {
        var normalized = text.ToLowerInvariant().Replace(" ", "");
        if (!normalized.Contains("currentsession") && !normalized.Contains("currentweek"))
            return [];

        var results = new List<int>();
        foreach (var line in text.Split('\n'))
        {
            var pct = PercentFromLine(line);
            if (pct.HasValue) results.Add(pct.Value);
        }
        return results;
    }

    // ── Reset Extract ───────────────────────────────────────────────

    private static string? ExtractResetNearLabel(string label, string text)
    {
        var lines = text.Split('\n');
        var normalizedLabel = NormalizeForSearch(label);

        for (int i = 0; i < lines.Length; i++)
        {
            if (!NormalizeForSearch(lines[i]).Contains(normalizedLabel))
                continue;

            for (int j = i; j < Math.Min(i + 14, lines.Length); j++)
            {
                var resetMatch = Regex.Match(lines[j], @"(Resets?[^\r\n]*)", RegexOptions.IgnoreCase);
                if (resetMatch.Success)
                    return resetMatch.Groups[1].Value.Trim().TrimEnd(')');
            }
        }
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string NormalizeForSearch(string text) =>
        new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string? ExtractFirst(string pattern, string text)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string StripAnsiCodes(string text) =>
        Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", "");

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "claude",
        ProviderName = "Claude",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Claude: {message}"
    };
}
