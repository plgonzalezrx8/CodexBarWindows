using CodexBarWindows.Abstractions;
using Microsoft.Toolkit.Uwp.Notifications;

namespace CodexBarWindows.Services;

public sealed class ToastNotificationSink : INotificationSink
{
    public void Show(string title, string message)
    {
        new ToastContentBuilder()
            .AddArgument("action", "viewDetails")
            .AddText(title)
            .AddText(message)
            .Show();
    }
}
