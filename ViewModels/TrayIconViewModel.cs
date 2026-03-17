using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CodexBarWindows.Models;
using CodexBarWindows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CodexBarWindows.ViewModels;

public class TrayIconViewModel : INotifyPropertyChanged
{
    private readonly IServiceProvider _serviceProvider;
    private string _tooltipText = "CodexBar";
    private SettingsWindow? _settingsWindow;

    public ObservableCollection<ProviderUsageStatus> ProviderStatuses { get; } = new();

    public string TooltipText
    {
        get => _tooltipText;
        set
        {
            if (_tooltipText != value)
            {
                _tooltipText = value;
                OnPropertyChanged();
            }
        }
    }

    public ICommand ShowSettingsCommand => new RelayCommand(ShowSettings);
    public ICommand ExitApplicationCommand => new RelayCommand(ExitApplication);

    public TrayIconViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private void ShowSettings(object? parameter)
    {
        // Bring existing window to front or create a new one
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            return;
        }

        var viewModel = _serviceProvider.GetRequiredService<SettingsViewModel>();
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ExitApplication(object? parameter)
    {
        _settingsWindow?.Close();
        Application.Current.Shutdown();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
