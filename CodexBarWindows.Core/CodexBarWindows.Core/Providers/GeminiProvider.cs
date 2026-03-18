using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Gemini provider — reads OAuth credentials from ~/.gemini/oauth_creds.json
/// (written by the Gemini CLI) and calls the Google Cloud Code Private API
/// to retrieve per-model quota buckets.
///
/// Supports automatic token refresh using the Gemini CLI's client ID/secret.
/// </summary>
public class GeminiProvider : IProviderProbe
{
    private readonly SettingsService _settings;
    private readonly IEnvironmentService _environmentService;
    private readonly HttpClient _httpClient;

    private const string QuotaEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const string LoadCodeAssistEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string TokenRefreshEndpoint = "https://oauth2.googleapis.com/token";
    private const string CredentialsRelPath = @".gemini\oauth_creds.json";
    private const string SettingsRelPath = @".gemini\settings.json";

    public string ProviderId => "gemini";
    public string ProviderName => "Gemini";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("gemini", false);

    public GeminiProvider(SettingsService settings, IEnvironmentService environmentService, HttpClient httpClient)
    {
        _settings = settings;
        _environmentService = environmentService;
        _httpClient = httpClient;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        var homeDir = _environmentService.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Check auth type from settings
        var authType = ReadAuthType(homeDir);
        if (authType is "api-key" or "vertex-ai")
            return MakeError($"Gemini {authType} auth not supported. Use Google account (OAuth) instead.");

        // Load OAuth credentials
        var creds = LoadCredentials(homeDir);
        if (creds == null)
            return MakeError("Not logged in to Gemini. Run `gemini` in Terminal to authenticate.");

        if (string.IsNullOrEmpty(creds.AccessToken))
            return MakeError("No Gemini access token found. Run `gemini` to log in.");

        var accessToken = creds.AccessToken;

        // Refresh if expired
        if (creds.ExpiryDate.HasValue && creds.ExpiryDate.Value < DateTime.UtcNow)
        {
            if (string.IsNullOrEmpty(creds.RefreshToken))
                return MakeError("Gemini token expired and no refresh token available. Run `gemini` to re-authenticate.");

            try
            {
                accessToken = await RefreshAccessToken(creds.RefreshToken, homeDir, cancellationToken);
            }
            catch (Exception ex)
            {
                return MakeError($"Token refresh failed: {ex.Message}");
            }
        }

        // Discover project ID via loadCodeAssist (needed for accurate quota)
        string? projectId = null;
        try
        {
            projectId = await DiscoverProjectId(accessToken, cancellationToken);
        }
        catch { /* Best effort — quota fetch may still work without it */ }

        // Fetch quota
        try
        {
            return await FetchQuota(accessToken, creds.IdToken, projectId, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return MakeError("Gemini session expired. Run `gemini` to re-authenticate.");
        }
        catch (Exception ex)
        {
            return MakeError($"Gemini API error: {ex.Message}");
        }
    }

    // ── Quota Fetch ─────────────────────────────────────────────────

    private async Task<ProviderUsageStatus> FetchQuota(
        string accessToken, string? idToken, string? projectId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, QuotaEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var requestBody = projectId != null
            ? JsonSerializer.Serialize(new { project = projectId })
            : "{}";
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var quotaResponse = JsonSerializer.Deserialize<GeminiQuotaResponse>(json, JsonOpts);

        if (quotaResponse?.Buckets == null || quotaResponse.Buckets.Count == 0)
            return MakeError("No quota buckets in Gemini response.");

        // Group by model, keep lowest remaining fraction per model
        var modelQuotas = new Dictionary<string, (double fraction, string? resetTime)>();
        foreach (var bucket in quotaResponse.Buckets)
        {
            if (string.IsNullOrEmpty(bucket.ModelId) || !bucket.RemainingFraction.HasValue)
                continue;

            if (!modelQuotas.TryGetValue(bucket.ModelId, out var existing) ||
                bucket.RemainingFraction.Value < existing.fraction)
            {
                modelQuotas[bucket.ModelId] = (bucket.RemainingFraction.Value, bucket.ResetTime);
            }
        }

        // Categorize into pro / flash / flash-lite (matching macOS reference)
        double proUsed = 0, flashUsed = 0, flashLiteUsed = 0;
        string? proReset = null, flashReset = null, flashLiteReset = null;
        bool hasPro = false, hasFlash = false, hasFlashLite = false;

        foreach (var (modelId, (fraction, resetTime)) in modelQuotas)
        {
            var lower = modelId.ToLowerInvariant();
            var usedPct = (1 - fraction) * 100;

            if (lower.Contains("pro"))
            {
                hasPro = true;
                if (usedPct > proUsed) { proUsed = usedPct; proReset = resetTime; }
            }
            else if (lower.Contains("flash-lite") || lower.Contains("flash_lite"))
            {
                hasFlashLite = true;
                if (usedPct > flashLiteUsed) { flashLiteUsed = usedPct; flashLiteReset = resetTime; }
            }
            else if (lower.Contains("flash"))
            {
                hasFlash = true;
                if (usedPct > flashUsed) { flashUsed = usedPct; flashReset = resetTime; }
            }
        }

        // Extract email from JWT id_token
        var email = ExtractEmailFromJwt(idToken);

        // Build tooltip with per-tier usage (always show tiers that have models)
        var tooltipParts = new List<string> { "Gemini" };
        tooltipParts.Add($"Models tracked: {modelQuotas.Count}");
        if (hasPro) tooltipParts.Add($"Pro: {proUsed:F1}% used{FormatReset(proReset)}");
        if (hasFlash) tooltipParts.Add($"Flash: {flashUsed:F1}% used{FormatReset(flashReset)}");
        if (hasFlashLite) tooltipParts.Add($"Flash Lite: {flashLiteUsed:F1}% used{FormatReset(flashLiteReset)}");
        if (email != null) tooltipParts.Add($"Account: {email}");

        // Use Pro usage for session bar, Flash for weekly bar (highest usage tier for icon)
        return new ProviderUsageStatus
        {
            ProviderId = "gemini",
            ProviderName = "Gemini",
            SessionProgress = proUsed / 100.0,
            WeeklyProgress = hasFlash ? flashUsed / 100.0 : (hasFlashLite ? flashLiteUsed / 100.0 : 0),
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private static string FormatReset(string? resetTime)
    {
        if (string.IsNullOrEmpty(resetTime)) return "";
        try
        {
            if (DateTime.TryParse(resetTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var resetDate))
            {
                var remaining = resetDate - DateTime.UtcNow;
                if (remaining.TotalMinutes <= 0) return " (resets soon)";
                if (remaining.TotalHours >= 1)
                    return $" (resets in {(int)remaining.TotalHours}h {remaining.Minutes}m)";
                return $" (resets in {remaining.Minutes}m)";
            }
        }
        catch { /* best effort */ }
        return "";
    }

    // ── Project ID Discovery ────────────────────────────────────────

    private async Task<string?> DiscoverProjectId(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoadCodeAssistEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            "{\"metadata\":{\"ideType\":\"GEMINI_CLI\",\"pluginType\":\"GEMINI\"}}",
            Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Try cloudaicompanionProject as string
        if (root.TryGetProperty("cloudaicompanionProject", out var projectProp))
        {
            if (projectProp.ValueKind == JsonValueKind.String)
            {
                var val = projectProp.GetString()?.Trim();
                if (!string.IsNullOrEmpty(val)) return val;
            }
            else if (projectProp.ValueKind == JsonValueKind.Object)
            {
                if (projectProp.TryGetProperty("id", out var idProp))
                {
                    var val = idProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(val)) return val;
                }
                if (projectProp.TryGetProperty("projectId", out var pidProp))
                {
                    var val = pidProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
        }

        return null;
    }

    // ── Token Refresh ───────────────────────────────────────────────

    private async Task<string> RefreshAccessToken(
        string refreshToken, string homeDir, CancellationToken ct)
    {
        // Find OAuth client credentials from the Gemini CLI installation
        var (clientId, clientSecret) = FindGeminiOAuthCredentials(homeDir);

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });

        using var response = await _httpClient.PostAsync(TokenRefreshEndpoint, body, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var newAccessToken = root.GetProperty("access_token").GetString()
            ?? throw new Exception("No access_token in refresh response");

        // Update stored credentials
        UpdateStoredCredentials(homeDir, root);

        return newAccessToken;
    }

    private static void UpdateStoredCredentials(string homeDir, JsonElement refreshResponse)
    {
        var credsPath = Path.Combine(homeDir, CredentialsRelPath);
        if (!File.Exists(credsPath)) return;

        try
        {
            var existingJson = File.ReadAllText(credsPath);
            using var existingDoc = JsonDocument.Parse(existingJson);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson) ?? [];

            if (refreshResponse.TryGetProperty("access_token", out var at))
                dict["access_token"] = at;
            if (refreshResponse.TryGetProperty("expires_in", out var ei))
            {
                var expiresIn = ei.GetDouble();
                var expiryMs = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(expiresIn * 1000));
                dict["expiry_date"] = JsonDocument.Parse(expiryMs.ToString()).RootElement;
            }
            if (refreshResponse.TryGetProperty("id_token", out var idt))
                dict["id_token"] = idt;

            var updatedJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(credsPath, updatedJson);
        }
        catch { /* Best effort */ }
    }

    // ── Credential Loading ──────────────────────────────────────────

    private class OAuthCredentials
    {
        public string? AccessToken { get; set; }
        public string? IdToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime? ExpiryDate { get; set; }
    }

    private static OAuthCredentials? LoadCredentials(string homeDir)
    {
        var credsPath = Path.Combine(homeDir, CredentialsRelPath);
        if (!File.Exists(credsPath)) return null;

        try
        {
            var json = File.ReadAllText(credsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            DateTime? expiryDate = null;
            if (root.TryGetProperty("expiry_date", out var expiryProp))
            {
                var expiryMs = expiryProp.GetDouble();
                expiryDate = DateTimeOffset.FromUnixTimeMilliseconds((long)expiryMs).UtcDateTime;
            }

            return new OAuthCredentials
            {
                AccessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null,
                IdToken = root.TryGetProperty("id_token", out var it) ? it.GetString() : null,
                RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                ExpiryDate = expiryDate
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadAuthType(string homeDir)
    {
        var settingsPath = Path.Combine(homeDir, SettingsRelPath);
        if (!File.Exists(settingsPath)) return null;

        try
        {
            var json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("security", out var sec) &&
                sec.TryGetProperty("auth", out var auth) &&
                auth.TryGetProperty("selectedType", out var sel))
            {
                return sel.GetString();
            }
        }
        catch { }
        return null;
    }

    // ── JWT Email Extraction ────────────────────────────────────────

    private static string? ExtractEmailFromJwt(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            var json = Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch { return null; }
    }

    // ── Gemini CLI OAuth Discovery ──────────────────────────────────

    private (string ClientId, string ClientSecret) FindGeminiOAuthCredentials(string homeDir)
    {
        // 1. Try reading client_id/client_secret from oauth_creds.json itself
        var credsPath = Path.Combine(homeDir, CredentialsRelPath);
        if (File.Exists(credsPath))
        {
            try
            {
                var json = File.ReadAllText(credsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("client_id", out var ci) &&
                    root.TryGetProperty("client_secret", out var cs))
                {
                    var clientId = ci.GetString();
                    var clientSecret = cs.GetString();
                    if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
                        return (clientId, clientSecret);
                }
            }
            catch { /* Fall through to JS file parsing */ }
        }

        // 2. Resolve the gemini binary to find the installation directory
        var oauthSubPath = Path.Combine("node_modules", "@google", "gemini-cli", "node_modules",
            "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js");
        var siblingSubPath = Path.Combine("node_modules", "@google", "gemini-cli-core",
            "dist", "src", "code_assist", "oauth2.js");

        var searchRoots = new List<string>();

        // 2a. Resolve the gemini binary by searching PATH (covers nvm, volta, fnm, etc.)
        var pathValue = _environmentService.GetEnvironmentVariable("PATH") ?? "";
        var extensions = new[] { ".ps1", ".cmd", ".bat", ".exe", "" };
        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), $"gemini{ext}");
                if (File.Exists(candidate))
                {
                    searchRoots.Add(dir.Trim());
                    goto doneSearch;
                }
            }
        }
        doneSearch:

        // 2b. Well-known static installation directories
        var appData = _environmentService.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = _environmentService.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        searchRoots.Add(Path.Combine(appData, "npm"));
        searchRoots.Add(Path.Combine(localAppData, "bun"));

        foreach (var root in searchRoots.Where(d => !string.IsNullOrEmpty(d)))
        {
            var nested = Path.Combine(root, oauthSubPath);
            if (File.Exists(nested))
            {
                var content = File.ReadAllText(nested);
                var res = ParseOAuthFromJs(content);
                if (res.HasValue) return res.Value;
            }
            var sibling = Path.Combine(root, siblingSubPath);
            if (File.Exists(sibling))
            {
                var content = File.ReadAllText(sibling);
                var res = ParseOAuthFromJs(content);
                if (res.HasValue) return res.Value;
            }
        }

        throw new Exception("Could not find Gemini OAuth client credentials. Ensure the Gemini CLI is installed and you have logged in with `gemini`.");
    }

    private static (string ClientId, string ClientSecret)? ParseOAuthFromJs(string content)
    {
        var idMatch = System.Text.RegularExpressions.Regex.Match(content, @"OAUTH_CLIENT_ID\s*=\s*['""]([^'""]+)['""]");
        var secretMatch = System.Text.RegularExpressions.Regex.Match(content, @"OAUTH_CLIENT_SECRET\s*=\s*['""]([^'""]+)['""]");

        if (idMatch.Success && secretMatch.Success)
            return (idMatch.Groups[1].Value, secretMatch.Groups[1].Value);
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "gemini",
        ProviderName = "Gemini",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Gemini: {message}"
    };
}

// ── Gemini Quota API Models ─────────────────────────────────────────

public class GeminiQuotaResponse
{
    [JsonPropertyName("buckets")]
    public List<GeminiQuotaBucket>? Buckets { get; set; }
}

public class GeminiQuotaBucket
{
    [JsonPropertyName("remainingFraction")]
    public double? RemainingFraction { get; set; }

    [JsonPropertyName("resetTime")]
    public string? ResetTime { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("tokenType")]
    public string? TokenType { get; set; }
}
