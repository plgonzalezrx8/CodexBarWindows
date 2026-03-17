using CodexBarWindows.Abstractions;
using Hardcodet.Wpf.TaskbarNotification;
using System.Windows;
using CodexBarWindows.ViewModels;

namespace CodexBarWindows.Services;

public sealed class TaskbarTrayPresenter : ITrayPresenter
{
    private readonly TrayIconViewModel _trayViewModel;
    private readonly IconGeneratorService _iconGenerator;

    public TaskbarTrayPresenter(TrayIconViewModel trayViewModel, IconGeneratorService iconGenerator)
    {
        _trayViewModel = trayViewModel;
        _iconGenerator = iconGenerator;
    }

    public void Present(RefreshResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _trayViewModel.ProviderStatuses.Clear();
            foreach (var status in result.Statuses)
            {
                _trayViewModel.ProviderStatuses.Add(status);
            }

            _trayViewModel.TooltipText = result.TooltipText;

            if (Application.Current.FindResource("NotifyIcon") is not TaskbarIcon taskbarIcon)
            {
                return;
            }

            taskbarIcon.Icon = _iconGenerator.GenerateMeterIcon(result.Statuses);
        });
    }
}
