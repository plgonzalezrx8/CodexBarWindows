using CodexBarWindows.Services;

namespace CodexBarWindows.Abstractions;

public interface ITrayPresenter
{
    void Present(RefreshResult result);
}
