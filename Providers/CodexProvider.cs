using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Codex provider — uses auth.json plus the Codex usage API.
/// </summary>
public class CodexProvider : IProviderProbe
{
    private readonly SettingsService _settings;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string ProviderId => "codex";
    public string ProviderName => "Codex";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("codex", true);

    public CodexProvider(SettingsService settings)
    {
        _settings = settings;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = LoadCredentials();
            try
            {
                var usage = await FetchUsageAsync(credentials, cancellationToken);
                return CreateStatus(usage);
            }
            catch (CodexUnauthorizedException)
            {
                if (!string.IsNullOrWhiteSpace(credentials.RefreshToken))
                {
                    var refreshed = await RefreshCredentialsAsync(credentials, cancellationToken);
                    var usage = await FetchUsageAsync(refreshed, cancellationToken);
                    return CreateStatus(usage);
                }

                return MakeError("Codex authentication expired. Run `codex login` again.");
            }
        }
        catch (FileNotFoundException ex)
        {
            return MakeError(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return MakeError(ex.Message);
        }
        catch (CodexRefreshException ex)
        {
            return MakeError(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return MakeError($"Codex API request failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return MakeError("Codex usage request timed out.");
        }
        catch (Exception ex)
        {
            return MakeError($"Codex usage fetch failed: {ex.Message}");
        }
    }

    private static ProviderUsageStatus CreateStatus(CodexUsageSnapshot usage)
    {
        var tooltipParts = new List<string> { "Codex" };
        tooltipParts.Add($"Session: {usage.SessionProgress * 100:F1}% used");
        tooltipParts.Add($"Weekly: {usage.WeeklyProgress * 100:F1}% used");
        if (usage.Credits.HasValue) tooltipParts.Add($"Credits: {usage.Credits.Value:F2}");
        if (!string.IsNullOrWhiteSpace(usage.SessionReset)) tooltipParts.Add($"Session resets: {usage.SessionReset}");
        if (!string.IsNullOrWhiteSpace(usage.WeeklyReset)) tooltipParts.Add($"Weekly resets: {usage.WeeklyReset}");

        return new ProviderUsageStatus
        {
            ProviderId = "codex",
            ProviderName = "Codex",
            SessionProgress = usage.SessionProgress,
            WeeklyProgress = usage.WeeklyProgress,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private static CodexCredentials LoadCredentials()
    {
        var authPath = Path.Combine(GetCodexHomePath(), "auth.json");
        if (!File.Exists(authPath))
        {
            throw new FileNotFoundException("Codex auth.json not found. Run `codex login` first.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(authPath));
        var root = document.RootElement;

        if (root.TryGetProperty("OPENAI_API_KEY", out var apiKeyElement))
        {
            var apiKey = apiKeyElement.GetString();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return new CodexCredentials(apiKey, string.Empty, null, null);
            }
        }

        if (!root.TryGetProperty("tokens", out var tokens))
        {
            throw new InvalidOperationException("Codex auth.json exists but contains no tokens. Run `codex login` again.");
        }

        var accessToken = tokens.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        var refreshToken = tokens.TryGetProperty("refresh_token", out var refreshTokenElement)
            ? refreshTokenElement.GetString()
            : null;
        var accountId = tokens.TryGetProperty("account_id", out var accountIdElement)
            ? accountIdElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Codex auth.json exists but contains no tokens. Run `codex login` again.");
        }

        return new CodexCredentials(accessToken, refreshToken ?? string.Empty, null, accountId);
    }

    private static async Task<CodexUsageSnapshot> FetchUsageAsync(CodexCredentials credentials, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveUsageUri());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodexBarWindows");

        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CodexUnauthorizedException();
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var sessionProgress = 0.0;
        var weeklyProgress = 0.0;
        string? sessionReset = null;
        string? weeklyReset = null;
        double? credits = null;

        if (root.TryGetProperty("rate_limit", out var rateLimit))
        {
            if (rateLimit.TryGetProperty("primary_window", out var primaryWindow))
            {
                sessionProgress = ReadProgress(primaryWindow);
                sessionReset = ReadReset(primaryWindow);
            }

            if (rateLimit.TryGetProperty("secondary_window", out var secondaryWindow))
            {
                weeklyProgress = ReadProgress(secondaryWindow);
                weeklyReset = ReadReset(secondaryWindow);
            }
        }

        if (root.TryGetProperty("credits", out var creditsElement))
        {
            credits = ReadBalance(creditsElement);
        }

        return new CodexUsageSnapshot(sessionProgress, weeklyProgress, credits, sessionReset, weeklyReset);
    }

    private static async Task<CodexCredentials> RefreshCredentialsAsync(CodexCredentials credentials, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["client_id"] = "app_EMoamEEZ73f0CkXaXp7hrann",
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credentials.RefreshToken,
                ["scope"] = "openid profile email"
            }),
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CodexRefreshException("Codex refresh token expired. Run `codex login` again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CodexRefreshException($"Codex token refresh failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshTokenElement)
            ? refreshTokenElement.GetString()
            : credentials.RefreshToken;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new CodexRefreshException("Codex token refresh returned no access token.");
        }

        return new CodexCredentials(accessToken, refreshToken ?? credentials.RefreshToken, null, credentials.AccountId);
    }

    private static Uri ResolveUsageUri()
    {
        var configPath = Path.Combine(GetCodexHomePath(), "config.toml");
        var baseUrl = "https://chatgpt.com/backend-api";

        if (File.Exists(configPath))
        {
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Split('#', 2)[0].Trim();
                if (!line.StartsWith("chatgpt_base_url", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var value = parts[1].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(value))
                {
                    baseUrl = value;
                    break;
                }
            }
        }

        baseUrl = NormalizeBaseUrl(baseUrl);
        var usagePath = baseUrl.Contains("/backend-api", StringComparison.OrdinalIgnoreCase)
            ? "/wham/usage"
            : "/api/codex/usage";

        return new Uri(baseUrl.TrimEnd('/') + usagePath);
    }

    private static string NormalizeBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = "https://chatgpt.com/backend-api";
        }

        trimmed = trimmed.TrimEnd('/');
        if ((trimmed.StartsWith("https://chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("https://chat.openai.com", StringComparison.OrdinalIgnoreCase)) &&
            !trimmed.Contains("/backend-api", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/backend-api";
        }

        return trimmed;
    }

    private static string GetCodexHomePath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return codexHome;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static double ReadProgress(JsonElement window)
    {
        if (window.TryGetProperty("used_percent", out var usedPercentElement) &&
            usedPercentElement.TryGetDouble(out var usedPercent))
        {
            return Math.Clamp(usedPercent / 100.0, 0.0, 1.0);
        }

        return 0.0;
    }

    private static string? ReadReset(JsonElement window)
    {
        if (window.TryGetProperty("reset_at", out var resetAtElement) &&
            resetAtElement.TryGetInt64(out var resetAt))
        {
            return DateTimeOffset.FromUnixTimeSeconds(resetAt).ToLocalTime().ToString("g");
        }

        return null;
    }

    private static double? ReadBalance(JsonElement creditsElement)
    {
        if (!creditsElement.TryGetProperty("balance", out var balanceElement))
        {
            return null;
        }

        if (balanceElement.ValueKind == JsonValueKind.Number && balanceElement.TryGetDouble(out var numericBalance))
        {
            return numericBalance;
        }

        if (balanceElement.ValueKind == JsonValueKind.String &&
            double.TryParse(balanceElement.GetString(), out var stringBalance))
        {
            return stringBalance;
        }

        return null;
    }

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "codex",
        ProviderName = "Codex",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Codex: {message}"
    };

    private sealed record CodexCredentials(string AccessToken, string RefreshToken, string? IdToken, string? AccountId);
    private sealed record CodexUsageSnapshot(double SessionProgress, double WeeklyProgress, double? Credits, string? SessionReset, string? WeeklyReset);

    private sealed class CodexUnauthorizedException : Exception;

    private sealed class CodexRefreshException(string message) : Exception(message);
}
