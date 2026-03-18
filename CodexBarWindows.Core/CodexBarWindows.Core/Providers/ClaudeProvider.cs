using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexBarWindows.Abstractions;
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
    private readonly ICommandRunner _commandRunner;
    private readonly IBrowserCookieSource _cookieSource;
    private readonly SettingsService _settings;
    private readonly ICredentialStore _credentialStore;
    private readonly IEnvironmentService _environmentService;
    private readonly HttpClient _httpClient;

    private const string OAuthUsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthTokenRefreshEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string DefaultOAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string OAuthBetaHeader = "oauth-2025-04-20";
    private const string CredentialsRelPath = @".claude\.credentials.json";

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
        _settings = settings;
        _credentialStore = credentialStore;
        _environmentService = environmentService;
        _httpClient = httpClient;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        // 1. Try OAuth credentials from Claude CLI (~/.claude/.credentials.json)
        try
        {
            var oauthResult = await TryFetchViaOAuth(cancellationToken);
            if (oauthResult != null)
                return oauthResult;
        }
        catch (Exception)
        {
            // Fall through to cookie-based auth
        }

        // 2. Try cookie-based auth (cached, browser import, manual)
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

        if (_commandRunner.CommandExists("claude"))
        {
            return MakeError("Claude CLI is installed but no valid credentials found. Run `claude` to authenticate via OAuth, or log in to claude.ai in Chrome or Edge.");
        }

        return MakeError("No Claude session found. Install Claude CLI and run `claude` to authenticate, or log in to claude.ai in Chrome or Edge.");
    }

    // ── OAuth Credential Flow ───────────────────────────────────────

    private async Task<ProviderUsageStatus?> TryFetchViaOAuth(CancellationToken cancellationToken)
    {
        var homeDir = _environmentService.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var creds = LoadOAuthCredentials(homeDir);
        if (creds == null)
            return null;

        var accessToken = creds.AccessToken;

        // Refresh if expired
        if (creds.ExpiresAt.HasValue && creds.ExpiresAt.Value < DateTime.UtcNow)
        {
            if (string.IsNullOrEmpty(creds.RefreshToken))
                return null; // No refresh token — fall through to other methods

            accessToken = await RefreshOAuthToken(creds.RefreshToken, homeDir, cancellationToken);
        }

        return await FetchOAuthUsage(accessToken, cancellationToken);
    }

    private async Task<ProviderUsageStatus> FetchOAuthUsage(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OAuthUsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", $"claude-code/{await DetectClaudeVersionAsync()}");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ClaudeAuthException("Claude OAuth token expired or invalid.");
        }

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        var sessionPercent = ReadUtilization(root, "five_hour");
        if (!sessionPercent.HasValue)
            throw new InvalidOperationException("Claude OAuth usage response missing five_hour data.");

        var weeklyPercent = ReadUtilization(root, "seven_day");
        var opusPercent = ReadUtilization(root, "seven_day_opus") ?? ReadUtilization(root, "seven_day_sonnet");

        var tooltipParts = new List<string> { "Claude (OAuth)" };
        tooltipParts.Add($"Session: {sessionPercent.Value:F1}% used");
        if (weeklyPercent.HasValue)
            tooltipParts.Add($"Weekly: {weeklyPercent.Value:F1}% used");
        if (opusPercent.HasValue)
            tooltipParts.Add($"Opus/Sonnet: {opusPercent.Value:F1}% used");

        var sessionReset = ReadReset(root, "five_hour");
        var weeklyReset = ReadReset(root, "seven_day");
        if (!string.IsNullOrWhiteSpace(sessionReset))
            tooltipParts.Add($"Session resets: {sessionReset}");
        if (!string.IsNullOrWhiteSpace(weeklyReset))
            tooltipParts.Add($"Weekly resets: {weeklyReset}");

        return new ProviderUsageStatus
        {
            ProviderId = "claude",
            ProviderName = "Claude",
            SessionProgress = Math.Clamp(sessionPercent.Value / 100.0, 0.0, 1.0),
            WeeklyProgress = weeklyPercent.HasValue
                ? Math.Clamp(weeklyPercent.Value / 100.0, 0.0, 1.0)
                : 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private async Task<string> RefreshOAuthToken(string refreshToken, string homeDir, CancellationToken cancellationToken)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = DefaultOAuthClientId,
            ["refresh_token"] = refreshToken
        });

        using var response = await _httpClient.PostAsync(OAuthTokenRefreshEndpoint, body, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var newAccessToken = root.GetProperty("access_token").GetString()
            ?? throw new Exception("No access_token in Claude OAuth refresh response");

        // Update stored credentials file
        UpdateOAuthCredentials(homeDir, root);

        return newAccessToken;
    }

    private sealed class ClaudeOAuthCreds
    {
        public string AccessToken { get; set; } = string.Empty;
        public string? RefreshToken { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    private static ClaudeOAuthCreds? LoadOAuthCredentials(string homeDir)
    {
        var credsPath = Path.Combine(homeDir, CredentialsRelPath);
        if (!File.Exists(credsPath)) return null;

        try
        {
            var json = File.ReadAllText(credsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Claude CLI stores OAuth under "claudeAiOauth" key
            if (!root.TryGetProperty("claudeAiOauth", out var oauth))
                return null;

            var accessToken = oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            string? refreshToken = oauth.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null;

            DateTime? expiresAt = null;
            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number)
            {
                var expiryMs = exp.GetDouble();
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)expiryMs).UtcDateTime;
            }

            return new ClaudeOAuthCreds
            {
                AccessToken = accessToken!,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt
            };
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateOAuthCredentials(string homeDir, JsonElement refreshResponse)
    {
        var credsPath = Path.Combine(homeDir, CredentialsRelPath);
        if (!File.Exists(credsPath)) return;

        try
        {
            var existingJson = File.ReadAllText(credsPath);
            using var existingDoc = JsonDocument.Parse(existingJson);
            if (!existingDoc.RootElement.TryGetProperty("claudeAiOauth", out var existingOauth))
                return;

            var oauthDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingOauth.GetRawText()) ?? [];

            if (refreshResponse.TryGetProperty("access_token", out var newAt))
                oauthDict["accessToken"] = newAt;
            if (refreshResponse.TryGetProperty("refresh_token", out var newRt))
                oauthDict["refreshToken"] = newRt;
            if (refreshResponse.TryGetProperty("expires_in", out var ei))
            {
                var expiresIn = ei.GetDouble();
                var expiryMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(expiresIn * 1000);
                oauthDict["expiresAt"] = JsonDocument.Parse(expiryMs.ToString()).RootElement;
            }

            var fullDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson) ?? [];
            fullDict["claudeAiOauth"] = JsonDocument.Parse(JsonSerializer.Serialize(oauthDict)).RootElement;

            var updatedJson = JsonSerializer.Serialize(fullDict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(credsPath, updatedJson);
        }
        catch { /* Best effort */ }
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

    private async Task<string> DetectClaudeVersionAsync()
    {
        try
        {
            var result = await _commandRunner.ExecuteCommandAsync("claude", "--version", timeoutMilliseconds: 3000);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
            {
                var version = result.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (!string.IsNullOrWhiteSpace(version) && char.IsDigit(version[0]))
                    return version;
            }
        }
        catch { /* Fall through to default */ }
        return "2.1.0";
    }

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "claude",
        ProviderName = "Claude",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Claude: {message}"
    };
}
