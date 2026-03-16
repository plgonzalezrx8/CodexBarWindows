using System.Drawing;
using System.Drawing.Drawing2D;
using CodexBarWindows.Models;

namespace CodexBarWindows.Services;

public class IconGeneratorService
{
    private readonly SettingsService _settingsService;

    public IconGeneratorService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public Icon GenerateMeterIcon(IEnumerable<ProviderUsageStatus> statuses)
    {
        var settings = _settingsService.CurrentSettings;
        var activeStatuses = statuses.Where(s => !s.IsError).ToList();

        if (activeStatuses.Count == 0)
        {
            // Fallback empty icon
            return GenerateSingleIcon(null);
        }

        if (settings.MergeIcons && activeStatuses.Count > 1)
        {
            return GenerateMergedIcon(activeStatuses);
        }
        else
        {
            // Just show the first active one, or the user's primary selected one if we track that
            return GenerateSingleIcon(activeStatuses.First());
        }
    }

    private Icon GenerateSingleIcon(ProviderUsageStatus? status)
    {
        int width = 16, height = 16;
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        if (status == null)
            return ConvertToIcon(bitmap);

        var (primaryColor, secondaryColor) = GetProviderColors(status.ProviderId, status.IsError);
        var bgColor = Color.FromArgb(80, 100, 100, 100);

        // Top bar (Session)
        using (var bgBrush = new SolidBrush(bgColor))
        {
            g.FillRectangle(bgBrush, 0, 2, width, 4);
        }
        using (var progressBrush = new SolidBrush(primaryColor))
        {
            int fillWidth = (int)(width * Math.Clamp(status.SessionProgress, 0, 1));
            if (fillWidth > 0)
                g.FillRectangle(progressBrush, 0, 2, fillWidth, 4);
        }

        // Bottom bar (Weekly)
        using (var bgBrush = new SolidBrush(bgColor))
        {
            g.FillRectangle(bgBrush, 0, 8, width, 2);
        }
        using (var progressBrush = new SolidBrush(secondaryColor))
        {
            int fillWidth = (int)(width * Math.Clamp(status.WeeklyProgress, 0, 1));
            if (fillWidth > 0)
                g.FillRectangle(progressBrush, 0, 8, fillWidth, 2);
        }

        return ConvertToIcon(bitmap);
    }

    private Icon GenerateMergedIcon(List<ProviderUsageStatus> statuses)
    {
        int width = 16, height = 16;
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Draw multiple thin vertical bars or stacked horizontal bars depending on count
        int barHeight = Math.Max(2, 12 / statuses.Count);
        int currentY = 2; // Start a little down

        foreach (var status in statuses.Take(6)) // Max 6 to fit in 16px roughly
        {
            var (primaryColor, _) = GetProviderColors(status.ProviderId, status.IsError);
            var bgColor = Color.FromArgb(80, 100, 100, 100);

            using (var bgBrush = new SolidBrush(bgColor))
            {
                g.FillRectangle(bgBrush, 0, currentY, width, barHeight - 1); // 1px spacing
            }
            using (var progressBrush = new SolidBrush(primaryColor))
            {
                int fillWidth = (int)(width * Math.Clamp(status.SessionProgress, 0, 1));
                if (fillWidth > 0)
                    g.FillRectangle(progressBrush, 0, currentY, fillWidth, barHeight - 1);
            }

            currentY += barHeight;
        }

        return ConvertToIcon(bitmap);
    }

    private (Color primary, Color secondary) GetProviderColors(string providerId, bool isError)
    {
        if (isError) return (Color.Gray, Color.DarkGray);

        return providerId.ToLowerInvariant() switch
        {
            "codex" => (Color.FromArgb(255, 30, 215, 96), Color.FromArgb(255, 15, 120, 50)), // Green
            "claude" => (Color.FromArgb(255, 230, 115, 53), Color.FromArgb(255, 120, 50, 20)), // Orange
            "cursor" => (Color.FromArgb(255, 0, 122, 255), Color.FromArgb(255, 0, 60, 130)), // Blue
            "gemini" => (Color.FromArgb(255, 66, 133, 244), Color.FromArgb(255, 234, 67, 53)), // Blue/Red
            "antigravity" => (Color.FromArgb(255, 200, 200, 200), Color.FromArgb(255, 100, 100, 100)), // White/Gray
            _ => (Color.LightBlue, Color.DarkBlue)
        };
    }

    private Icon ConvertToIcon(Bitmap bitmap)
    {
        nint hIcon = bitmap.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
