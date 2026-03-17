using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class CodexProviderTests
{
    [Fact]
    public async Task Uses_api_key_credentials_when_present()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = new FakeEnvironmentService();
        var codexHome = Path.Combine(paths.AppDataDirectory, "codex-home");
        Directory.CreateDirectory(codexHome);
        env.Variables["CODEX_HOME"] = codexHome;
        env.Folders[Environment.SpecialFolder.UserProfile] = codexHome;
        await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), """{"OPENAI_API_KEY":"sk-test"}""");
        AuthenticationHeaderValue? authHeader = null;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            authHeader = request.Headers.Authorization;
            return Json("""{"rate_limit":{"primary_window":{"used_percent":10},"secondary_window":{"used_percent":30}}}""");
        }));

        var provider = new CodexProvider(settings, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal("Bearer", authHeader?.Scheme);
        Assert.Equal("sk-test", authHeader?.Parameter);
    }

    [Fact]
    public async Task Refreshes_expired_access_token_and_retries_usage()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = new FakeEnvironmentService();
        var codexHome = Path.Combine(paths.AppDataDirectory, "codex-home");
        Directory.CreateDirectory(codexHome);
        env.Variables["CODEX_HOME"] = codexHome;
        env.Folders[Environment.SpecialFolder.UserProfile] = codexHome;
        await File.WriteAllTextAsync(
            Path.Combine(codexHome, "auth.json"),
            """{"tokens":{"access_token":"expired","refresh_token":"refresh","account_id":"acct_123"}}""");

        var usageCalls = 0;
        var refreshCalls = 0;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                refreshCalls++;
                return Json("""{"access_token":"fresh","refresh_token":"refresh"}""");
            }

            usageCalls++;
            return usageCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json("""{"rate_limit":{"primary_window":{"used_percent":42,"reset_at":1773741600},"secondary_window":{"used_percent":12}},"credits":{"balance":"9.50"}}""");
        }));

        var provider = new CodexProvider(settings, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal(2, usageCalls);
        Assert.Equal(1, refreshCalls);
        Assert.Contains("Credits: 9.50", status.TooltipText);
    }

    [Fact]
    public async Task Returns_error_when_refresh_response_has_no_access_token()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = new FakeEnvironmentService();
        var codexHome = Path.Combine(paths.AppDataDirectory, "codex-home");
        Directory.CreateDirectory(codexHome);
        env.Variables["CODEX_HOME"] = codexHome;
        env.Folders[Environment.SpecialFolder.UserProfile] = codexHome;
        await File.WriteAllTextAsync(
            Path.Combine(codexHome, "auth.json"),
            """{"tokens":{"access_token":"expired","refresh_token":"refresh"}}""");

        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""{"refresh_token":"refresh"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }));

        var provider = new CodexProvider(settings, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("returned no access token", status.TooltipText);
    }

    [Fact]
    public async Task Returns_error_for_missing_tokens_and_missing_auth_file()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var env = new FakeEnvironmentService();
        var codexHome = Path.Combine(paths.AppDataDirectory, "codex-home");
        Directory.CreateDirectory(codexHome);
        env.Variables["CODEX_HOME"] = codexHome;
        env.Folders[Environment.SpecialFolder.UserProfile] = codexHome;
        await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), """{"tokens":{}}""");
        var provider = new CodexProvider(settings, env, new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())));

        var invalidStatus = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(invalidStatus.IsError);
        Assert.Contains("contains no tokens", invalidStatus.TooltipText);

        File.Delete(Path.Combine(codexHome, "auth.json"));

        var missingStatus = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(missingStatus.IsError);
        Assert.Contains("auth.json not found", missingStatus.TooltipText);
    }

    [Fact]
    public void Parse_usage_snapshot_handles_missing_fields_and_string_balances()
    {
        using var document = JsonDocument.Parse("""{"credits":{"balance":"12.34"}}""");

        var usage = CodexProvider.ParseUsageSnapshot(document.RootElement);
        var status = CodexProvider.CreateStatus(usage);

        Assert.Equal(0, usage.SessionProgress);
        Assert.Equal(0, usage.WeeklyProgress);
        Assert.Equal(12.34, usage.Credits);
        Assert.Contains("Session: 0.0% used", status.TooltipText);
        Assert.Contains("Weekly: 0.0% used", status.TooltipText);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
