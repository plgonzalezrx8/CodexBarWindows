using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CodexBarWindows.ViewModels;

public class TrayIconViewModel : INotifyPropertyChanged
{
    private string _tooltipText = "CodexBar";

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

    private void ShowSettings(object? parameter)
    {
        // TODO: Show Settings Window
    }

    private void ExitApplication(object? parameter)
    {
        System.Windows.Application.Current.Shutdown();
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
