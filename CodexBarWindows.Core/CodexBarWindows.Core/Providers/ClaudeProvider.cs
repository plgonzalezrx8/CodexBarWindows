using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Claude provider — reads Claude OAuth credentials from ~/.claude/.credentials.json
/// and falls back to claude.ai browser cookies or a manual sessionKey cookie.
///
/// Auth priority:
///   1. Claude OAuth file
///   2. Browser cookies (for Claude web dashboard)
///   3. Manual sessionKey cookie
/// </summary>
public class ClaudeProvider : IProviderProbe
{
    private readonly ICommandRunner _commandRunner;
    private readonly IBrowserCookieSource _cookieSource;
    private readonly IEnvironmentService _environmentService;
    private readonly SettingsService _settings;
    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;

    public string ProviderId => "claude";
    public string ProviderName => "Claude";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("claude", true);

    public ClaudeProvider(
        ICommandRunner commandRunner,
        IBrowserCookieSource cookieSource,
        SettingsService settings,
        ICredentialStore credentialStore,
        IEnvironmentService environmentService,
        HttpClient httpClient)
    {
        _commandRunner = commandRunner;
        _cookieSource = cookieSource;
        _environmentService = environmentService;
        _settings = settings;
        _credentialStore = credentialStore;
        _httpClient = httpClient;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        var homeDir = _environmentService.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var cached = _credentialStore.GetCachedCookieHeader("claude");
        if (cached != null && !string.IsNullOrWhiteSpace(cached.CookieHeader))
        {
            try
            {
                return await FetchWithCookieHeader(cached.CookieHeader, cancellationToken);
            }
            catch (ClaudeAuthException)
            {
                _credentialStore.ClearCachedCookieHeader("claude");
            }
        }

        var importedCookieHeader = _cookieSource.GetCookieHeader("claude.ai");
        if (!string.IsNullOrWhiteSpace(importedCookieHeader))
        {
            try
            {
                var status = await FetchWithCookieHeader(importedCookieHeader, cancellationToken);
                var normalizedCookieHeader = NormalizeCookieHeader(importedCookieHeader);
                if (normalizedCookieHeader != null)
                {
                    _credentialStore.CacheCookieHeader("claude", normalizedCookieHeader, "browser-auto");
                }
                return status;
            }
            catch (ClaudeAuthException)
            {
            }
        }

        var manualCookie = _credentialStore.GetCredential("claude", "manual-cookie");
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

        try
        {
            var oauthStatus = await TryFetchOAuthStatusAsync(homeDir, cancellationToken);
            if (oauthStatus != null)
            {
                return oauthStatus;
            }
        }
        catch (ClaudeAuthException ex)
        {
            return MakeError(ex.Message);
        }

        if (_commandRunner.CommandExists("claude"))
        {
            return MakeError("Claude CLI is installed, but no valid Claude OAuth credentials were found. Run `claude` to authenticate, or log in to claude.ai in Chrome, Edge, or Brave.");
        }

        return MakeError("No Claude session found. Log in to claude.ai in Chrome, Edge, or Brave.");
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

    private async Task<ProviderUsageStatus?> TryFetchOAuthStatusAsync(string homeDir, CancellationToken cancellationToken)
    {
        var credentialsPath = GetOAuthCredentialsPath(homeDir);
        if (!File.Exists(credentialsPath))
        {
            return null;
        }

        var credentials = LoadOAuthCredentials(credentialsPath);
        if (credentials.ExpiresAt.HasValue && credentials.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
            {
                throw new ClaudeAuthException("Claude OAuth token expired and no refresh token was found. Run `claude` to re-authenticate.");
            }

            credentials = await RefreshOAuthCredentialsAsync(credentialsPath, credentials, cancellationToken);
        }

        return await FetchWithOAuthAccessTokenAsync(credentials, cancellationToken);
    }

    private async Task<ProviderUsageStatus> FetchWithOAuthAccessTokenAsync(
        ClaudeOAuthCredentialsSnapshot credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ClaudeAuthException("Claude OAuth session expired. Run `claude` to re-authenticate.");
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var usage = JsonSerializer.Deserialize<ClaudeOAuthUsageResponse>(json, JsonOptions)
            ?? throw new ClaudeAuthException("Claude OAuth response was invalid.");

        var sessionWindow = usage.FiveHour;
        var weeklyWindow = usage.SevenDay ?? usage.SevenDayOAuthApps ?? usage.SevenDaySonnet ?? usage.SevenDayOpus;
        if (sessionWindow == null && weeklyWindow == null)
        {
            throw new ClaudeAuthException("Claude OAuth response was invalid.");
        }

        var tooltipParts = new List<string> { "Claude" };
        if (sessionWindow?.Utilization.HasValue == true)
        {
            tooltipParts.Add($"Session: {sessionWindow.Utilization.Value:F1}% used");
        }
        if (weeklyWindow?.Utilization.HasValue == true)
        {
            tooltipParts.Add($"Weekly: {weeklyWindow.Utilization.Value:F1}% used");
        }
        if (usage.SevenDaySonnet?.Utilization.HasValue == true && weeklyWindow != usage.SevenDaySonnet)
        {
            tooltipParts.Add($"Sonnet: {usage.SevenDaySonnet.Utilization.Value:F1}% used");
        }
        if (usage.SevenDayOpus?.Utilization.HasValue == true && weeklyWindow != usage.SevenDayOpus)
        {
            tooltipParts.Add($"Opus: {usage.SevenDayOpus.Utilization.Value:F1}% used");
        }
        if (usage.ExtraUsage?.IsEnabled == true &&
            usage.ExtraUsage.MonthlyLimit.HasValue &&
            usage.ExtraUsage.UsedCredits.HasValue)
        {
            tooltipParts.Add($"Extra: {usage.ExtraUsage.UsedCredits.Value:F2}/{usage.ExtraUsage.MonthlyLimit.Value:F2}");
        }
        if (!string.IsNullOrWhiteSpace(credentials.RateLimitTier))
        {
            tooltipParts.Add($"Tier: {credentials.RateLimitTier}");
        }

        return new ProviderUsageStatus
        {
            ProviderId = "claude",
            ProviderName = "Claude",
            SessionProgress = sessionWindow?.Utilization.HasValue == true
                ? Math.Clamp(sessionWindow.Utilization.Value / 100.0, 0.0, 1.0)
                : 0.0,
            WeeklyProgress = weeklyWindow?.Utilization.HasValue == true
                ? Math.Clamp(weeklyWindow.Utilization.Value / 100.0, 0.0, 1.0)
                : 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private async Task<ClaudeOAuthCredentialsSnapshot> RefreshOAuthCredentialsAsync(
        string credentialsPath,
        ClaudeOAuthCredentialsSnapshot credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://platform.claude.com/v1/oauth/token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken ?? string.Empty,
            ["client_id"] = GetOAuthClientId()
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ClaudeAuthException(
                $"Claude OAuth token refresh failed with HTTP {(int)response.StatusCode}{FormatResponseSuffix(body)}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ClaudeAuthException("Claude OAuth token refresh returned no access token.");
        }

        var refreshToken = root.TryGetProperty("refresh_token", out var refreshTokenElement)
            ? refreshTokenElement.GetString()
            : credentials.RefreshToken;

        var expiresAt = credentials.ExpiresAt;
        if (root.TryGetProperty("expires_in", out var expiresInElement) &&
            expiresInElement.TryGetInt32(out var expiresInSeconds) &&
            expiresInSeconds > 0)
        {
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);
        }

        var updated = credentials with
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt
        };

        TryPersistOAuthCredentials(credentialsPath, updated);
        return updated;
    }

    private static ClaudeOAuthCredentialsSnapshot LoadOAuthCredentials(string credentialsPath)
    {
        try
        {
            var json = File.ReadAllText(credentialsPath);
            var file = JsonSerializer.Deserialize<ClaudeOAuthCredentialsFile>(json, JsonOptions)
                ?? throw new ClaudeAuthException("Claude OAuth credentials file is invalid.");
            var oauth = file.ClaudeAiOauth
                ?? throw new ClaudeAuthException("Claude OAuth credentials file is missing the claudeAiOauth section.");

            var accessToken = oauth.AccessToken?.Trim();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new ClaudeAuthException("Claude OAuth access token is missing. Run `claude` to authenticate.");
            }

            var refreshToken = oauth.RefreshToken?.Trim();
            DateTimeOffset? expiresAt = oauth.ExpiresAt.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt.Value)
                : null;

            return new ClaudeOAuthCredentialsSnapshot(
                accessToken,
                refreshToken,
                expiresAt,
                oauth.Scopes ?? [],
                oauth.RateLimitTier);
        }
        catch (ClaudeAuthException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ClaudeAuthException($"Claude OAuth credentials file could not be read: {ex.Message}");
        }
    }

    private static void TryPersistOAuthCredentials(string credentialsPath, ClaudeOAuthCredentialsSnapshot credentials)
    {
        try
        {
            var file = new ClaudeOAuthCredentialsFile
            {
                ClaudeAiOauth = new ClaudeOAuthCredentialsData
                {
                    AccessToken = credentials.AccessToken,
                    RefreshToken = credentials.RefreshToken,
                    ExpiresAt = credentials.ExpiresAt.HasValue
                        ? credentials.ExpiresAt.Value.ToUnixTimeMilliseconds()
                        : null,
                    Scopes = credentials.Scopes.ToList(),
                    RateLimitTier = credentials.RateLimitTier
                }
            };

            File.WriteAllText(credentialsPath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch
        {
            // Best effort only. The in-memory token is still usable for this run.
        }
    }

    private string GetOAuthClientId()
    {
        var configured = _environmentService.GetEnvironmentVariable("CODEXBAR_CLAUDE_OAUTH_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    }

    private static string GetOAuthCredentialsPath(string homeDir) =>
        Path.Combine(homeDir, ".claude", ".credentials.json");

    private static string FormatResponseSuffix(string body)
    {
        var trimmed = body.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var shortened = trimmed.Length > 200 ? trimmed[..200] + "..." : trimmed;
        return $" ({shortened})";
    }

    private async Task<ClaudeOrganizationInfo> FetchOrganizationAsync(string sessionKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai/api/organizations");
        request.Headers.TryAddWithoutValidation("Cookie", $"sessionKey={sessionKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
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

    private async Task<ClaudeWebUsageData> FetchUsageAsync(string sessionKey, string organizationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://claude.ai/api/organizations/{organizationId}/usage");
        request.Headers.TryAddWithoutValidation("Cookie", $"sessionKey={sessionKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
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

    internal static double? ReadUtilization(JsonElement root, string propertyName)
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

    internal static string? NormalizeCookieHeader(string? cookieHeader)
    {
        var sessionKey = ExtractSessionKey(cookieHeader);
        return string.IsNullOrWhiteSpace(sessionKey) ? null : $"sessionKey={sessionKey}";
    }

    internal static string? ExtractSessionKey(string? cookieHeader)
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
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private sealed record ClaudeOAuthCredentialsSnapshot(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        IReadOnlyList<string> Scopes,
        string? RateLimitTier);

    private sealed class ClaudeOAuthCredentialsFile
    {
        public ClaudeOAuthCredentialsData? ClaudeAiOauth { get; set; }
    }

    private sealed class ClaudeOAuthCredentialsData
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public long? ExpiresAt { get; set; }
        public List<string>? Scopes { get; set; }
        public string? RateLimitTier { get; set; }
    }

    private sealed class ClaudeOAuthUsageResponse
    {
        [JsonPropertyName("five_hour")]
        public ClaudeOAuthUsageWindow? FiveHour { get; set; }

        [JsonPropertyName("seven_day")]
        public ClaudeOAuthUsageWindow? SevenDay { get; set; }

        [JsonPropertyName("seven_day_oauth_apps")]
        public ClaudeOAuthUsageWindow? SevenDayOAuthApps { get; set; }

        [JsonPropertyName("seven_day_opus")]
        public ClaudeOAuthUsageWindow? SevenDayOpus { get; set; }

        [JsonPropertyName("seven_day_sonnet")]
        public ClaudeOAuthUsageWindow? SevenDaySonnet { get; set; }

        [JsonPropertyName("extra_usage")]
        public ClaudeOAuthExtraUsage? ExtraUsage { get; set; }
    }

    private sealed class ClaudeOAuthUsageWindow
    {
        [JsonPropertyName("utilization")]
        public double? Utilization { get; set; }

        [JsonPropertyName("resets_at")]
        public string? ResetsAt { get; set; }
    }

    private sealed class ClaudeOAuthExtraUsage
    {
        [JsonPropertyName("is_enabled")]
        public bool? IsEnabled { get; set; }

        [JsonPropertyName("monthly_limit")]
        public double? MonthlyLimit { get; set; }

        [JsonPropertyName("used_credits")]
        public double? UsedCredits { get; set; }

        [JsonPropertyName("utilization")]
        public double? Utilization { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }
    }

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
