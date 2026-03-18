using System.Net;
using System.Text;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class AntigravityProviderTests
{
    [Fact]
    public async Task FetchStatusAsync_parses_plan_account_and_model_usage()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var commandRunner = new FakeCommandRunner
        {
            Handler = (_, _, _, _) => new CommandResult(
                0,
                "1234|C:\\Program Files\\Antigravity\\language_server_windows.exe --csrf_token token-123 --api_server_port 4567",
                string.Empty)
        };

        string? seenToken = null;
        string? seenPath = null;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            seenToken = request.Headers.GetValues("X-Codeium-Csrf-Token").Single();
            seenPath = request.RequestUri!.AbsolutePath;
            return Json("""
                {
                  "code": 0,
                  "userStatus": {
                    "email": "dev@example.com",
                    "planStatus": {
                      "planInfo": {
                        "planDisplayName": "Pro"
                      }
                    },
                    "cascadeModelConfigData": {
                      "clientModelConfigs": [
                        {
                          "label": "Claude Sonnet",
                          "modelOrAlias": { "model": "claude-sonnet-4" },
                          "quotaInfo": { "remainingFraction": 0.2 }
                        },
                        {
                          "label": "Pro",
                          "modelOrAlias": { "model": "pro" },
                          "quotaInfo": { "remainingFraction": 0.4 }
                        }
                      ]
                    }
                  }
                }
                """);
        }));

        var provider = new AntigravityProvider(commandRunner, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal("token-123", seenToken);
        Assert.Equal("/exa.language_server_pb.LanguageServerService/GetUserStatus", seenPath);
        Assert.Contains("Plan: Pro", status.TooltipText);
        Assert.Contains("Account: dev@example.com", status.TooltipText);
        Assert.Contains("Claude Sonnet: 80.0% used", status.TooltipText);
        Assert.Contains("Pro: 60.0% used", status.TooltipText);
        Assert.Equal(0.8, status.SessionProgress, 3);
        Assert.Equal(0.6, status.WeeklyProgress, 3);
    }

    [Fact]
    public async Task FetchStatusAsync_returns_error_when_process_is_missing()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var commandRunner = new FakeCommandRunner
        {
            Handler = (_, _, _, _) => new CommandResult(0, string.Empty, string.Empty)
        };
        var client = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called when process detection fails.")));

        var provider = new AntigravityProvider(commandRunner, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("language server not detected", status.TooltipText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchStatusAsync_falls_back_to_command_model_response_when_user_status_fails()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var commandRunner = new FakeCommandRunner
        {
            Handler = (_, _, _, _) => new CommandResult(
                0,
                "1234|C:\\Program Files\\Antigravity\\language_server_windows.exe --csrf_token token-456 --api_server_port 4567",
                string.Empty)
        };

        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/exa.language_server_pb.LanguageServerService/GetUserStatus" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "/exa.language_server_pb.LanguageServerService/GetCommandModelConfigs" => Json("""
                    {
                      "clientModelConfigs": [
                        {
                          "label": "Flash",
                          "quotaInfo": { "remainingFraction": 0.2 }
                        },
                        {
                          "label": "Claude",
                          "quotaInfo": { "remainingFraction": 0.6 }
                        }
                      ]
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected path: {request.RequestUri.AbsolutePath}")
            };
        }));

        var provider = new AntigravityProvider(commandRunner, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("Models: 2 tracked", status.TooltipText);
        Assert.Contains("Flash: 80.0% used", status.TooltipText);
        Assert.Equal(0.8, status.SessionProgress, 3);
    }

    [Theory]
    [InlineData(
        @"C:\Program Files\Antigravity\language_server_windows.exe --csrf_token abc123 --api_server_port 4567",
        4567, "abc123")]
    [InlineData(
        @"C:\Antigravity\bin\language_server_windows.exe --app_data_dir antigravity --csrf_token tok99 --api_server_port 9999",
        9999, "tok99")]
    public void TryParseCommandLine_extracts_port_and_csrf_from_antigravity_processes(
        string commandLine, int expectedPort, string expectedToken)
    {
        var result = AntigravityProvider.TryParseCommandLine(commandLine);

        Assert.NotNull(result);
        Assert.Equal(expectedPort, result.Port);
        Assert.Equal(expectedToken, result.CsrfToken);
    }

    [Fact]
    public void TryParseCommandLine_returns_zero_port_when_random_port_used()
    {
        var result = AntigravityProvider.TryParseCommandLine(
            @"C:\Antigravity\bin\language_server_windows.exe --csrf_token tok --random_port --app_data_dir antigravity");

        Assert.NotNull(result);
        Assert.Equal(0, result.Port);
        Assert.Equal("tok", result.CsrfToken);
    }

    [Theory]
    [InlineData("notepad.exe --csrf_token abc --api_server_port 1234")]
    [InlineData(@"C:\Antigravity\language_server_windows.exe --api_server_port 4567")]
    [InlineData(@"C:\Windsurf\extensions\language_server.exe --csrf_token tok --extension_server_port 9999")]
    public void TryParseCommandLine_rejects_non_antigravity_or_incomplete_commands(string commandLine)
    {
        var result = AntigravityProvider.TryParseCommandLine(commandLine);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(@"--app_data_dir antigravity --csrf_token tok", true)]
    [InlineData(@"c:\programs\antigravity\bin\lang.exe", true)]
    [InlineData(@"c:\programs\windsurf\bin\lang.exe", false)]
    [InlineData(@"--app_data_dir codeium --csrf_token tok", false)]
    public void IsAntigravityProcess_filters_correctly(string lowerCmd, bool expected)
    {
        Assert.Equal(expected, AntigravityProvider.IsAntigravityProcess(lowerCmd));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
