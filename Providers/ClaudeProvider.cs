using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly BrowserCookieService _cookieService;
    private readonly SettingsService _settings;
    private readonly CredentialManagerService _credentialManager;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string ProviderId => "claude";
    public string ProviderName => "Claude";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("claude", true);

    public ClaudeProvider(
        CliExecutionHelper cliHelper,
        BrowserCookieService cookieService,
        SettingsService settings,
        CredentialManagerService credentialManager)
    {
        _cliHelper = cliHelper;
        _cookieService = cookieService;
        _settings = settings;
        _credentialManager = credentialManager;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        var cached = _credentialManager.GetCachedCookieHeader("claude");
        if (cached != null && !string.IsNullOrWhiteSpace(cached.CookieHeader))
        {
            try
            {
                return await FetchWithCookieHeader(cached.CookieHeader, cancellationToken);
            }
            catch (ClaudeAuthException)
            {
                _credentialManager.ClearCachedCookieHeader("claude");
            }
        }

        var importedCookieHeader = _cookieService.GetCookieHeader("claude.ai");
        if (!string.IsNullOrWhiteSpace(importedCookieHeader))
        {
            try
            {
                var status = await FetchWithCookieHeader(importedCookieHeader, cancellationToken);
                var normalizedCookieHeader = NormalizeCookieHeader(importedCookieHeader);
                if (normalizedCookieHeader != null)
                {
                    _credentialManager.CacheCookieHeader("claude", normalizedCookieHeader, "browser-auto");
                }
                return status;
            }
            catch (ClaudeAuthException)
            {
            }
        }

        var manualCookie = _credentialManager.GetCredential("claude", "manual-cookie");
        if (!string.IsNullOrWhiteSpace(manualCookie))
        {
            try
            {
                return await FetchWithCookieHeader(manualCookie, cancellationToken);
            }
            catch (ClaudeAuthException)
            {
            }
        }

        if (_cliHelper.CommandExists("claude"))
        {
            return MakeError("Claude CLI is installed, but Windows usage fetching currently relies on a claude.ai session. Log in to claude.ai in Chrome or Edge, or save a manual sessionKey cookie.");
        }

        return MakeError("No Claude session found. Log in to claude.ai in Chrome or Edge.");
    }

    private async Task<ProviderUsageStatus> FetchWithCookieHeader(string cookieHeader, CancellationToken cancellationToken)
    {
        var sessionKey = ExtractSessionKey(cookieHeader);
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new ClaudeAuthException("No Claude session key found.");
        }

        var organization = await FetchOrganizationAsync(sessionKey, cancellationToken);
        var usage = await FetchUsageAsync(sessionKey, organization.Id, cancellationToken);

        var tooltipParts = new List<string> { "Claude" };
        tooltipParts.Add($"Session: {usage.SessionPercentUsed:F1}% used");
        if (usage.WeeklyPercentUsed.HasValue)
            tooltipParts.Add($"Weekly: {usage.WeeklyPercentUsed.Value:F1}% used");
        if (usage.OpusPercentUsed.HasValue)
            tooltipParts.Add($"Opus/Sonnet: {usage.OpusPercentUsed.Value:F1}% used");
        if (!string.IsNullOrWhiteSpace(usage.SessionReset))
            tooltipParts.Add($"Session resets: {usage.SessionReset}");
        if (!string.IsNullOrWhiteSpace(usage.WeeklyReset))
            tooltipParts.Add($"Weekly resets: {usage.WeeklyReset}");

        return new ProviderUsageStatus
        {
            ProviderId = "claude",
            ProviderName = "Claude",
            SessionProgress = Math.Clamp(usage.SessionPercentUsed / 100.0, 0.0, 1.0),
            WeeklyProgress = usage.WeeklyPercentUsed.HasValue
                ? Math.Clamp(usage.WeeklyPercentUsed.Value / 100.0, 0.0, 1.0)
                : 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private static async Task<ClaudeOrganizationInfo> FetchOrganizationAsync(string sessionKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai/api/organizations");
        request.Headers.TryAddWithoutValidation("Cookie", $"sessionKey={sessionKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ClaudeAuthException("Claude session expired.");
        }

        response.EnsureSuccessStatusCode();

        var organizations = JsonSerializer.Deserialize<List<ClaudeOrganizationResponse>>(
            await response.Content.ReadAsStringAsync(cancellationToken),
            JsonOptions);

        if (organizations == null || organizations.Count == 0)
        {
            throw new InvalidOperationException("Claude organizations response was invalid.");
        }

        var selected = organizations.FirstOrDefault(o => o.HasChatCapability)
            ?? organizations.FirstOrDefault(o => !o.IsApiOnly)
            ?? organizations[0];

        if (string.IsNullOrWhiteSpace(selected.Uuid))
        {
            throw new InvalidOperationException("Claude organization id was missing.");
        }

        return new ClaudeOrganizationInfo(selected.Uuid, selected.Name);
    }

    private static async Task<ClaudeWebUsageData> FetchUsageAsync(string sessionKey, string organizationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://claude.ai/api/organizations/{organizationId}/usage");
        request.Headers.TryAddWithoutValidation("Cookie", $"sessionKey={sessionKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ClaudeAuthException("Claude session expired.");
        }

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        var sessionPercent = ReadUtilization(root, "five_hour");
        if (!sessionPercent.HasValue)
        {
            throw new InvalidOperationException("Claude usage response was invalid.");
        }

        var weeklyPercent = ReadUtilization(root, "seven_day");
        var opusPercent = ReadUtilization(root, "seven_day_opus") ?? ReadUtilization(root, "seven_day_sonnet");

        return new ClaudeWebUsageData(
            sessionPercent.Value,
            ReadReset(root, "five_hour"),
            weeklyPercent,
            ReadReset(root, "seven_day"),
            opusPercent);
    }

    private static double? ReadUtilization(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window))
        {
            return null;
        }

        if (!window.TryGetProperty("utilization", out var utilizationElement))
        {
            return null;
        }

        if (utilizationElement.ValueKind == JsonValueKind.Number && utilizationElement.TryGetDouble(out var numericValue))
        {
            return numericValue;
        }

        return null;
    }

    private static string? ReadReset(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window) ||
            !window.TryGetProperty("resets_at", out var resetElement))
        {
            return null;
        }

        var raw = resetElement.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(raw, out var parsed))
        {
            return parsed.ToLocalTime().ToString("g");
        }

        return raw;
    }

    private static string? NormalizeCookieHeader(string? cookieHeader)
    {
        var sessionKey = ExtractSessionKey(cookieHeader);
        return string.IsNullOrWhiteSpace(sessionKey) ? null : $"sessionKey={sessionKey}";
    }

    private static string? ExtractSessionKey(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        foreach (var rawSegment in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segment = rawSegment.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
                ? rawSegment[7..].Trim()
                : rawSegment.Trim();

            if (!segment.StartsWith("sessionKey=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = segment["sessionKey=".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ClaudeOrganizationInfo(string Id, string? Name);
    private sealed record ClaudeWebUsageData(double SessionPercentUsed, string? SessionReset, double? WeeklyPercentUsed, string? WeeklyReset, double? OpusPercentUsed);

    private sealed class ClaudeOrganizationResponse
    {
        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("capabilities")]
        public List<string>? Capabilities { get; set; }

        public bool HasChatCapability => Capabilities?.Any(c => string.Equals(c, "chat", StringComparison.OrdinalIgnoreCase)) == true;

        public bool IsApiOnly => Capabilities is { Count: > 0 } caps && caps.All(c => string.Equals(c, "api", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ClaudeAuthException : Exception
    {
        public ClaudeAuthException(string message) : base(message)
        {
        }
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
