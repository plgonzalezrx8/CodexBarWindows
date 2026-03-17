using System.Drawing;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.IntegrationTests;

public class IconGeneratorServiceTests
{
    [Fact]
    public void Generates_visible_progress_bars_for_single_provider()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var service = new IconGeneratorService(settings);

        using var icon = service.GenerateMeterIcon(
        [
            new ProviderUsageStatus
            {
                ProviderId = "codex",
                ProviderName = "Codex",
                SessionProgress = 1.0,
                WeeklyProgress = 1.0
            }
        ]);

        using var bitmap = icon.ToBitmap();

        Assert.Equal(16, bitmap.Width);
        Assert.Equal(16, bitmap.Height);
        Assert.NotEqual(Color.Transparent.ToArgb(), bitmap.GetPixel(0, 2).ToArgb());
        Assert.NotEqual(Color.Transparent.ToArgb(), bitmap.GetPixel(0, 8).ToArgb());
    }

    [Fact]
    public void Generates_merged_icon_when_merge_is_enabled()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.MergeIcons = true;
        var service = new IconGeneratorService(settings);

        using var icon = service.GenerateMeterIcon(
        [
            new ProviderUsageStatus
            {
                ProviderId = "codex",
                ProviderName = "Codex",
                SessionProgress = 1.0
            },
            new ProviderUsageStatus
            {
                ProviderId = "claude",
                ProviderName = "Claude",
                SessionProgress = 0.75
            }
        ]);

        using var bitmap = icon.ToBitmap();

        Assert.Equal(16, bitmap.Width);
        Assert.Equal(16, bitmap.Height);
        Assert.NotEqual(Color.Transparent.ToArgb(), bitmap.GetPixel(0, 10).ToArgb());
    }
}
