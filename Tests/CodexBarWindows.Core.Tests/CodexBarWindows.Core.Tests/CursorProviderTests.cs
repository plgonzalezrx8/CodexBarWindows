using System.Net;
using System.Text;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class CursorProviderTests
{
    [Fact]
    public async Task Empty_membership_type_does_not_add_plan_label_or_throw()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var cookies = new FakeCookieSource
        {
            CookieHeader = "WorkosCursorSessionToken=session"
        };
        var credentials = new FakeCredentialStore();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith("/api/usage-summary", StringComparison.Ordinal))
            {
                return Json("""{"membershipType":"","billingCycleEnd":"2026-03-31","individualUsage":{"plan":{"used":250,"limit":1000},"onDemand":{"used":0,"limit":500}}}""");
            }

            return Json("""{"email":"dev@example.com"}""");
        }));

        var provider = new CursorProvider(cookies, credentials, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.DoesNotContain("Plan: Cursor", status.TooltipText);
        Assert.Contains("Account: dev@example.com", status.TooltipText);
    }

    [Fact]
    public async Task Plan_usage_under_one_percent_is_not_scaled_to_hundred_percent()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var cookies = new FakeCookieSource
        {
            CookieHeader = "WorkosCursorSessionToken=session"
        };
        var credentials = new FakeCredentialStore();
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith("/api/usage-summary", StringComparison.Ordinal))
            {
                return Json("""{"membershipType":"pro","billingCycleEnd":"2026-03-31","individualUsage":{"plan":{"used":10,"limit":2000},"onDemand":{"used":0,"limit":500}}}""");
            }

            return Json("""{"email":"dev@example.com"}""");
        }));

        var provider = new CursorProvider(cookies, credentials, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("Plan: 0.5% used", status.TooltipText);
        Assert.Equal(0.005, status.SessionProgress, 3);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
