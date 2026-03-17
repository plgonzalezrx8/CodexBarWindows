namespace CodexBarWindows.Abstractions;

public interface INotificationSink
{
    void Show(string title, string message);
}
