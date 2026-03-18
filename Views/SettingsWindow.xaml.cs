using System.Windows;
using CodexBarWindows.ViewModels;

namespace CodexBarWindows.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _viewModel.SaveCompleted += OnSaveCompleted;
    }

    private void OnSaveCompleted()
    {
        Dispatcher.Invoke(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.SaveCompleted -= OnSaveCompleted;
        base.OnClosed(e);
    }
}
