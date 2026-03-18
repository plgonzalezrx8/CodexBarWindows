using System.Net;
using System.Text;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class ClaudeProviderTests
{
    [Fact]
    public async Task Uses_cached_cookie_before_browser_cookie()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = CreateClaudeEnvironment(paths);
        var commandRunner = new FakeCommandRunner();
        var cookies = new FakeCookieSource { CookieHeader = "sessionKey=browser-cookie" };
        var credentials = new FakeCredentialStore();
        credentials.CacheCookieHeader("claude", "sessionKey=cached-cookie", "test");
        string? seenCookie = null;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            seenCookie = request.Headers.GetValues("Cookie").Single();
            return request.RequestUri!.AbsoluteUri.Contains("/organizations/", StringComparison.OrdinalIgnoreCase)
                ? Json("""{"five_hour":{"utilization":25,"resets_at":"2026-03-17T10:00:00Z"}}""")
                : Json("""[{"uuid":"org_123","name":"Primary","capabilities":["chat"]}]""");
        }));

        var provider = new ClaudeProvider(commandRunner, cookies, settings, credentials, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal("sessionKey=cached-cookie", seenCookie);
    }

    [Fact]
    public async Task Clears_expired_cached_cookie_and_uses_imported_cookie()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = CreateClaudeEnvironment(paths);
        var commandRunner = new FakeCommandRunner();
        var cookies = new FakeCookieSource { CookieHeader = "foo=1; sessionKey=browser-cookie; x=2" };
        var credentials = new FakeCredentialStore();
        credentials.CacheCookieHeader("claude", "sessionKey=expired-cookie", "test");
        var requests = new List<string>();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var cookie = request.Headers.GetValues("Cookie").Single();
            requests.Add(cookie);

            if (cookie.Contains("expired-cookie", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return request.RequestUri!.AbsoluteUri.Contains("/organizations/", StringComparison.OrdinalIgnoreCase)
                ? Json("""{"five_hour":{"utilization":42,"resets_at":"2026-03-17T10:00:00Z"}}""")
                : Json("""[{"uuid":"org_123","name":"Primary","capabilities":["chat"]}]""");
        }));

        var provider = new ClaudeProvider(commandRunner, cookies, settings, credentials, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("browser-cookie", requests.Last());
        Assert.Equal("sessionKey=browser-cookie", credentials.GetCachedCookieHeader("claude")?.CookieHeader);
    }

    [Fact]
    public async Task Returns_error_when_cookie_has_no_session_key_and_cli_exists()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = CreateClaudeEnvironment(paths);
        var commandRunner = new FakeCommandRunner { Exists = true };
        var cookies = new FakeCookieSource { CookieHeader = "foo=bar" };
        var credentials = new FakeCredentialStore();
        var provider = new ClaudeProvider(commandRunner, cookies, settings, credentials, env, new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())));

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("Claude CLI is installed", status.TooltipText);
    }

    [Fact]
    public async Task Chooses_chat_capable_organization_and_formats_usage()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = CreateClaudeEnvironment(paths);
        var commandRunner = new FakeCommandRunner();
        var cookies = new FakeCookieSource { CookieHeader = "sessionKey=browser-cookie" };
        var credentials = new FakeCredentialStore();
        var requestedUsageUris = new List<string>();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/organizations/", StringComparison.OrdinalIgnoreCase))
            {
                requestedUsageUris.Add(request.RequestUri.AbsoluteUri);
                return Json("""{"five_hour":{"utilization":20,"resets_at":"2026-03-17T10:00:00Z"},"seven_day":{"utilization":40,"resets_at":"2026-03-20T10:00:00Z"},"seven_day_sonnet":{"utilization":60}}""");
            }

            return Json("[{\"uuid\":\"org_api\",\"name\":\"API\",\"capabilities\":[\"api\"]},{\"uuid\":\"org_chat\",\"name\":\"Chat\",\"capabilities\":[\"chat\"]}]");
        }));

        var provider = new ClaudeProvider(commandRunner, cookies, settings, credentials, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("/organizations/org_chat/usage", requestedUsageUris.Single());
        Assert.Contains("Session: 20.0% used", status.TooltipText);
        Assert.Contains("Weekly: 40.0% used", status.TooltipText);
        Assert.Contains("Opus/Sonnet: 60.0% used", status.TooltipText);
    }

    [Fact]
    public async Task Uses_claude_oauth_credentials_file_when_browser_cookie_is_missing()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = CreateClaudeEnvironment(paths);
        var commandRunner = new FakeCommandRunner();
        var cookies = new FakeCookieSource();
        var credentials = new FakeCredentialStore();

        var claudeDir = Path.Combine(env.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        Directory.CreateDirectory(claudeDir);
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ".credentials.json"),
            """
            {
              "claudeAiOauth": {
                "accessToken": "access-token",
                "refreshToken": "refresh-token",
                "expiresAt": 4102444800000,
                "scopes": ["user:profile"],
                "rateLimitTier": "pro"
              }
            }
            """);

        string? seenAuth = null;
        string? seenBeta = null;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            seenAuth = request.Headers.Authorization?.ToString();
            seenBeta = request.Headers.TryGetValues("anthropic-beta", out var betaValues)
                ? betaValues.SingleOrDefault()
                : null;

            return Json("""
                {
                  "five_hour": { "utilization": 25, "resets_at": "2026-03-17T10:00:00Z" },
                  "seven_day": { "utilization": 50, "resets_at": "2026-03-20T10:00:00Z" }
                }
                """);
        }));

        var provider = new ClaudeProvider(commandRunner, cookies, settings, credentials, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal("Bearer access-token", seenAuth);
        Assert.Equal("oauth-2025-04-20", seenBeta);
        Assert.Contains("Session: 25.0% used", status.TooltipText);
        Assert.Contains("Weekly: 50.0% used", status.TooltipText);
    }

    [Fact]
    public void Read_utilization_and_cookie_helpers_handle_missing_data()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"five_hour":{"utilization":12.5}}""");

        Assert.Equal(12.5, ClaudeProvider.ReadUtilization(document.RootElement, "five_hour"));
        Assert.Null(ClaudeProvider.ReadUtilization(document.RootElement, "missing"));
        Assert.Equal("sessionKey=abc123", ClaudeProvider.NormalizeCookieHeader("Cookie: foo=1; sessionKey=abc123"));
        Assert.Null(ClaudeProvider.NormalizeCookieHeader("foo=1"));
        Assert.Null(ClaudeProvider.ExtractSessionKey(null));
    }

    private static FakeEnvironmentService CreateClaudeEnvironment(TestAppDataPaths paths)
    {
        var env = new FakeEnvironmentService();
        var homeDir = Path.Combine(paths.AppDataDirectory, "home");
        Directory.CreateDirectory(homeDir);
        env.Folders[Environment.SpecialFolder.UserProfile] = homeDir;
        return env;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
