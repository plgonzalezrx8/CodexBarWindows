using System.Windows;
using CodexBarWindows.ViewModels;

namespace CodexBarWindows.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
    }
}
