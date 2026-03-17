using CodexBarWindows.Services;

namespace CodexBarWindows.IntegrationTests;

public class CliExecutionHelperTests
{
    [Fact]
    public async Task Executes_commands_and_captures_output()
    {
        var helper = new CliExecutionHelper();

        var result = await helper.ExecuteCommandAsync("cmd.exe", "/c echo hello");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Times_out_long_running_commands()
    {
        var helper = new CliExecutionHelper();

        var result = await helper.ExecuteCommandAsync("powershell.exe", "-NoProfile -Command \"Start-Sleep -Seconds 2\"", timeoutMilliseconds: 100);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
