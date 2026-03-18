using System.Net;
using System.Text;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class GeminiProviderTests
{
    [Fact]
    public async Task Returns_error_for_unsupported_auth_type()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["gemini"] = true;
        var env = new FakeEnvironmentService();
        var homeDir = Path.Combine(paths.AppDataDirectory, "home");
        Directory.CreateDirectory(Path.Combine(homeDir, ".gemini"));
        env.Folders[Environment.SpecialFolder.UserProfile] = homeDir;
        env.Folders[Environment.SpecialFolder.ApplicationData] = homeDir;
        env.Folders[Environment.SpecialFolder.LocalApplicationData] = homeDir;
        await File.WriteAllTextAsync(
            Path.Combine(homeDir, ".gemini", "settings.json"),
            """{"security":{"auth":{"selectedType":"api-key"}}}""");

        var provider = new GeminiProvider(settings, env, new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())));

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("api-key auth not supported", status.TooltipText);
    }

    [Fact]
    public async Task Returns_error_when_not_logged_in()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["gemini"] = true;
        var env = CreateGeminiEnvironment(paths);
        var provider = new GeminiProvider(settings, env, new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())));

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("Not logged in", status.TooltipText);
    }

    [Fact]
    public async Task Fetches_quota_from_valid_oauth_credentials()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["gemini"] = true;
        var env = CreateGeminiEnvironment(paths);
        var geminiDir = Path.Combine(env.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
        var jwtPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"email":"gemini@example.com"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await File.WriteAllTextAsync(
            Path.Combine(geminiDir, "oauth_creds.json"),
            $$"""{"access_token":"access","id_token":"x.{{jwtPayload}}.y","refresh_token":"refresh","expiry_date":4102444800000}""");

        var client = new HttpClient(new StubHttpMessageHandler(_ =>
            Json("""{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":0.25},{"modelId":"gemini-2.5-flash","remainingFraction":0.5}]}""")));

        var provider = new GeminiProvider(settings, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("Pro: 75.0% used", status.TooltipText);
        Assert.Contains("Flash: 50.0% used", status.TooltipText);
        Assert.Contains("gemini@example.com", status.TooltipText);
        Assert.Equal(0.75, status.SessionProgress, 3);
        Assert.Equal(0.5, status.WeeklyProgress, 3);
    }

    [Fact]
    public async Task Refreshes_expired_token_using_stored_client_credentials()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["gemini"] = true;
        var env = CreateGeminiEnvironment(paths);
        var geminiDir = Path.Combine(env.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
        await File.WriteAllTextAsync(
            Path.Combine(geminiDir, "oauth_creds.json"),
            """{"access_token":"expired","refresh_token":"refresh","client_id":"client","client_secret":"secret","expiry_date":0}""");

        var requests = new List<string>();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsoluteUri.Contains("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""{"access_token":"fresh","expires_in":3600}""");
            }

            return Json("""{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":0.4}]}""");
        }));

        var provider = new GeminiProvider(settings, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains(requests, uri => uri.Contains("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Pro: 60.0% used", status.TooltipText);
    }

    [Fact]
    public async Task Refreshes_expired_token_using_gemini_cli_install_layout()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["gemini"] = true;
        var env = CreateGeminiEnvironment(paths);
        var geminiDir = Path.Combine(env.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
        await File.WriteAllTextAsync(
            Path.Combine(geminiDir, "oauth_creds.json"),
            """{"access_token":"expired","refresh_token":"refresh","expiry_date":0}""");

        var installRoot = Path.Combine(paths.AppDataDirectory, "nodejs");
        var oauthDir = Path.Combine(installRoot, "node_modules", "@google", ".gemini-cli-test", "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist");
        Directory.CreateDirectory(oauthDir);
        await File.WriteAllTextAsync(
            Path.Combine(oauthDir, "oauth2.js"),
            """
            const OAUTH_CLIENT_ID = "client";
            const OAUTH_CLIENT_SECRET = "secret";
            """);
        env.Variables["PATH"] = installRoot;

        var requests = new List<string>();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsoluteUri.Contains("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""{"access_token":"fresh","expires_in":3600}""");
            }

            return Json("""{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":0.4}]}""");
        }));

        var provider = new GeminiProvider(settings, env, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains(requests, uri => uri.Contains("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Pro: 60.0% used", status.TooltipText);
    }

    private static FakeEnvironmentService CreateGeminiEnvironment(TestAppDataPaths paths)
    {
        var env = new FakeEnvironmentService();
        var homeDir = Path.Combine(paths.AppDataDirectory, "home");
        Directory.CreateDirectory(Path.Combine(homeDir, ".gemini"));
        env.Folders[Environment.SpecialFolder.UserProfile] = homeDir;
        env.Folders[Environment.SpecialFolder.ApplicationData] = homeDir;
        env.Folders[Environment.SpecialFolder.LocalApplicationData] = homeDir;
        return env;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
