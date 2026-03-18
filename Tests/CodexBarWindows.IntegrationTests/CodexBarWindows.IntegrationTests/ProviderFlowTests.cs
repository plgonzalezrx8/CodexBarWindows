using System.Net;
using System.Text;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.IntegrationTests;

public class ProviderFlowTests
{
    [Fact]
    public async Task Codex_provider_refreshes_after_unauthorized_response()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = new FakeEnvironmentService();
        var homeDir = Path.Combine(paths.AppDataDirectory, "codex-home");
        Directory.CreateDirectory(homeDir);
        env.Variables["CODEX_HOME"] = homeDir;
        env.Folders[Environment.SpecialFolder.UserProfile] = homeDir;
        await File.WriteAllTextAsync(
            Path.Combine(homeDir, "auth.json"),
            """
            {
              "tokens": {
                "access_token": "expired",
                "refresh_token": "refresh-token",
                "account_id": "acct_123"
              }
            }
            """);

        var calls = 0;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            calls++;
            if (request.RequestUri!.AbsoluteUri.Contains("oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"fresh-token","refresh_token":"refresh-token"}""", Encoding.UTF8, "application/json")
                };
            }

            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "codex-usage.json")), Encoding.UTF8, "application/json")
            };
        }));

        var provider = new CodexProvider(settings, env, client);
        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("Credits: 12.50", status.TooltipText);
    }

    [Fact]
    public async Task Cursor_provider_uses_browser_cookie_and_maps_usage()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var cookies = new FakeCookieSource { CookieHeader = "WorkosCursorSessionToken=test" };
        var credentials = new FakeCredentialStore();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/usage-summary" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"billingCycleEnd":"2026-03-31","membershipType":"pro","individualUsage":{"plan":{"used":500,"limit":1000},"onDemand":{"used":50,"limit":100}}}""", Encoding.UTF8, "application/json")
                },
                "/api/auth/me" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"email":"cursor@example.com"}""", Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }))
        {
            BaseAddress = new Uri("https://cursor.com")
        };

        var provider = new CursorProvider(cookies, credentials, settings, client);
        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("cursor@example.com", status.TooltipText);
        Assert.True(status.SessionProgress > 0.4);
    }

    [Fact]
    public async Task Augment_provider_uses_cached_cookie_and_maps_usage()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var cookies = new FakeCookieSource();
        var credentials = new FakeCredentialStore();
        credentials.CacheCookieHeader("augment", "session=test", "test");
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/credits" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"usageUnitsRemaining":80,"usageUnitsConsumedThisBillingCycle":20}""", Encoding.UTF8, "application/json")
                },
                "/api/subscription" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"planName":"Team","email":"augment@example.com"}""", Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var provider = new AugmentProvider(settings, cookies, credentials, client);
        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("augment@example.com", status.TooltipText);
        Assert.Contains("Team", status.TooltipText);
    }

    [Fact]
    public async Task Claude_provider_falls_back_from_expired_cached_cookie_to_browser_cookie()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var commandRunner = new FakeCommandRunner();
        var cookies = new FakeCookieSource { CookieHeader = "sessionKey=browser-cookie" };
        var credentials = new FakeCredentialStore();
        credentials.CacheCookieHeader("claude", "sessionKey=expired-cookie", "test");
        var seenCookies = new List<string>();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var cookie = request.Headers.GetValues("Cookie").Single();
            seenCookies.Add(cookie);

            if (cookie.Contains("expired-cookie", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return request.RequestUri!.AbsoluteUri.Contains("/organizations/", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"five_hour":{"utilization":25},"seven_day":{"utilization":50}}""", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"uuid":"org_123","capabilities":["chat"]}]""", Encoding.UTF8, "application/json")
                };
        }));

        var env = new FakeEnvironmentService();
        env.Folders[Environment.SpecialFolder.UserProfile] = Path.Combine(Path.GetTempPath(), "codexbar-test-nonexistent");
        var provider = new ClaudeProvider(commandRunner, cookies, settings, credentials, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal(2, seenCookies.Distinct().Count());
        Assert.Equal("sessionKey=browser-cookie", credentials.GetCachedCookieHeader("claude")?.CookieHeader);
    }

    [Fact]
    public async Task Cursor_provider_reports_auth_failure_when_no_cookie_is_available()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var cookies = new FakeCookieSource();
        var credentials = new FakeCredentialStore();
        var provider = new CursorProvider(cookies, credentials, settings, new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())));

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("Log in", status.TooltipText);
    }

    [Fact]
    public async Task Cursor_provider_handles_empty_membership_type_without_throwing()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var cookies = new FakeCookieSource { CookieHeader = "WorkosCursorSessionToken=test" };
        var credentials = new FakeCredentialStore();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/usage-summary" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"billingCycleEnd":"2026-03-31","membershipType":"","individualUsage":{"plan":{"used":500,"limit":1000}}}""", Encoding.UTF8, "application/json")
                },
                "/api/auth/me" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"email":"cursor@example.com"}""", Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }))
        {
            BaseAddress = new Uri("https://cursor.com")
        };

        var provider = new CursorProvider(cookies, credentials, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.DoesNotContain("Plan: Cursor ", status.TooltipText);
    }
}
