using NHotkey;
using NHotkey.Wpf;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CodexBarWindows.Models;

namespace CodexBarWindows.Services;

public class GlobalHotkeyService
{
    private readonly SettingsService _settingsService;

    public GlobalHotkeyService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += BindFromSettings;
        
        // Ensure binding is initialized on the UI thread since NHotkey needs a Window/Hwnd
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(BindFromSettings);
        }
    }

    private void BindFromSettings()
    {
        var shortcut = _settingsService.CurrentSettings.GlobalShortcut;
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            try { HotkeyManager.Current.Remove("OpenTray"); } catch {}
            return;
        }

        try
        {
            var converter = new KeyGestureConverter();
            if (converter.ConvertFromString(shortcut) is KeyGesture gesture)
            {
                HotkeyManager.Current.AddOrReplace("OpenTray", gesture.Key, gesture.Modifiers, OnHotkeyPressed);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to bind hotkey '{shortcut}': {ex.Message}");
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var taskbarIcon = Application.Current.FindResource("NotifyIcon");
            if (taskbarIcon != null)
            {
                // Try executing the built-in click command or showing the popup
                var showMethod = taskbarIcon.GetType().GetMethod("ShowTrayPopup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (showMethod != null)
                {
                    showMethod.Invoke(taskbarIcon, null);
                }
                else
                {
                    // Fallback: search for the actual Popup and open it
                    var popupProp = taskbarIcon.GetType().GetProperty("TrayPopupResolved", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (popupProp != null && popupProp.GetValue(taskbarIcon) is Popup popup)
                    {
                        popup.IsOpen = true;
                    }
                }
            }
        });
        e.Handled = true;
    }
}
