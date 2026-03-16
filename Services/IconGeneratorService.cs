using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CodexBarWindows.Services;

public class IconGeneratorService
{
    // Generates a tiny two-bar meter icon based on progress fractions (0.0 to 1.0)
    // Top bar: session progress
    // Bottom bar: weekly progress
    public Icon GenerateMeterIcon(double sessionProgress, double weeklyProgress, bool isError = false)
    {
        int width = 16;
        int height = 16;
        
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Define colors
        var sessionColor = isError ? Color.Gray : Color.LightBlue;
        var weeklyColor = isError ? Color.DarkGray : Color.DarkBlue;
        var bgColor = Color.FromArgb(100, 100, 100, 100);

        // Top bar (Session) - Thicker
        int sessionBarY = 2;
        int sessionBarHeight = 4;
        
        // Background for top bar
        using (var bgBrush = new SolidBrush(bgColor))
        {
            g.FillRectangle(bgBrush, 0, sessionBarY, width, sessionBarHeight);
        }
        
        // Progress for top bar
        using (var progressBrush = new SolidBrush(sessionColor))
        {
            int fillWidth = (int)(width * Math.Clamp(sessionProgress, 0, 1));
            if (fillWidth > 0)
                g.FillRectangle(progressBrush, 0, sessionBarY, fillWidth, sessionBarHeight);
        }

        // Bottom bar (Weekly) - Hairline
        int weeklyBarY = 8;
        int weeklyBarHeight = 2;

        // Background for bottom bar
        using (var bgBrush = new SolidBrush(bgColor))
        {
            g.FillRectangle(bgBrush, 0, weeklyBarY, width, weeklyBarHeight);
        }

        // Progress for bottom bar
        using (var progressBrush = new SolidBrush(weeklyColor))
        {
            int fillWidth = (int)(width * Math.Clamp(weeklyProgress, 0, 1));
            if (fillWidth > 0)
                g.FillRectangle(progressBrush, 0, weeklyBarY, fillWidth, weeklyBarHeight);
        }

        // Convert Bitmap to Icon
        nint hIcon = bitmap.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
