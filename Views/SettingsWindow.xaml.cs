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
        // Save() is invoked via ICommand from a button click on the UI thread,
        // so SaveCompleted fires on the UI thread — no need for Dispatcher.Invoke.
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.SaveCompleted -= OnSaveCompleted;
        base.OnClosed(e);
    }
}
