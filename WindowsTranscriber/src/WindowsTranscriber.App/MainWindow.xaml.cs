using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using WindowsTranscriber.App.Services;
using WindowsTranscriber.App.ViewModels;
using WindowsTranscriber.Audio.Processes;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TranscriptSearchService _searchService = new();
    private readonly GlobalHotkeyService _globalHotkeyService = new();
    private readonly TrayIconService _trayIconService = new();

    private string _lastSearchQuery = string.Empty;
    private double _manualScrollOffset;
    private bool _isClosing;
    private bool _closeFinalized;
    private bool _exitRequested;
    private bool _trayHintShown;
    private bool _hotkeyFailureShown;
    private WindowState _lastNonMinimizedWindowState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(
            new ProcessScanner(),
            new LiveTranscriptionCoordinator());
        DataContext = _viewModel;

        SourceInitialized += Window_SourceInitialized;
        StateChanged += Window_StateChanged;
        _globalHotkeyService.Pressed += OnGlobalHotkeyPressed;
        _trayIconService.ShowRequested += ShowFromTray;
        _trayIconService.ToggleRequested += OnTrayToggleRequested;
        _trayIconService.ExitRequested += ExitFromTray;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.TranscriptionStarted += OnTranscriptionStarted;
        _viewModel.NotificationRequested += OnNotificationRequested;
        _viewModel.ThemeChanged += ThemeManager.Apply;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged +=
            OnSystemUserPreferenceChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshApplicationsAsync();
        await _viewModel.LoadSettingsAsync();
        ApplySavedWindowPlacement();
        ConfigureDesktopServices();
        await _viewModel.LoadHistoryAsync();
        await _viewModel.RestoreRecoveryIfAvailableAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeFinalized)
        {
            return;
        }

        if (!_exitRequested && _viewModel.MinimizeToTray && !_isClosing)
        {
            e.Cancel = true;
            SaveWindowPlacement();
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIconService.ShowNotification(
                    "WindowsTranscriber is still running",
                    "Use the tray icon to show or exit the app.");
            }

            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        SaveWindowPlacement();
        IsEnabled = false;

        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _globalHotkeyService.Dispose();
            _trayIconService.Dispose();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -=
                OnSystemUserPreferenceChanged;
            _closeFinalized = true;
            Close();
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _globalHotkeyService.Attach(this);
        ConfigureDesktopServices();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _lastNonMinimizedWindowState = WindowState;
        }

        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
        {
            Hide();
        }
    }

    private async void OnGlobalHotkeyPressed()
    {
        if (!IsVisible &&
            !_viewModel.IsTranscriptionActive &&
            !_viewModel.CanStartTranscription)
        {
            ShowFromTray();
        }

        await _viewModel.ToggleListeningFromHotkeyAsync();
    }

    private async void OnTrayToggleRequested() =>
        await _viewModel.ToggleListeningFromHotkeyAsync();

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

    private void ExitFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            _exitRequested = true;
            Show();
            Close();
        });
    }

    private void OnTranscriptionStarted()
    {
        if (_viewModel.MinimizeWhileTranscribing)
        {
            WindowState = WindowState.Minimized;
        }
    }

    private void OnNotificationRequested(string title, string message) =>
        _trayIconService.ShowNotification(title, message);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.GlobalHotkeyEnabled) or
            nameof(MainViewModel.MinimizeToTray) or
            nameof(MainViewModel.NotificationsEnabled))
        {
            ConfigureDesktopServices();
        }

        if (e.PropertyName is nameof(MainViewModel.IsTranscribing) or
            nameof(MainViewModel.IsStartingTranscription))
        {
            _trayIconService.UpdateTranscriptionState(
                _viewModel.IsTranscriptionActive);
        }
    }

    private void ConfigureDesktopServices()
    {
        _trayIconService.Configure(
            _viewModel.MinimizeToTray,
            _viewModel.NotificationsEnabled);
        _trayIconService.UpdateTranscriptionState(
            _viewModel.IsTranscriptionActive);
        var hotkeyReady = _globalHotkeyService.SetEnabled(
            _viewModel.GlobalHotkeyEnabled);
        if (_viewModel.GlobalHotkeyEnabled && !hotkeyReady && !_hotkeyFailureShown)
        {
            _hotkeyFailureShown = true;
            _trayIconService.ShowNotification(
                "Global hotkey unavailable",
                "Ctrl+Shift+Space is already used by another application.");
        }
        else if (!_viewModel.GlobalHotkeyEnabled || hotkeyReady)
        {
            _hotkeyFailureShown = false;
        }

        ThemeManager.Apply(_viewModel.SelectedThemeOption.Mode);
    }

    private void OnSystemUserPreferenceChanged(
        object sender,
        Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_viewModel.SelectedThemeOption.Mode == AppThemeMode.System)
        {
            _ = Dispatcher.InvokeAsync(() => ThemeManager.Apply(AppThemeMode.System));
        }
    }

    private void ApplySavedWindowPlacement()
    {
        var placement = _viewModel.WindowPlacement;
        if (placement is null)
        {
            return;
        }

        var isVisible = placement.Left + placement.Width > SystemParameters.VirtualScreenLeft &&
            placement.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            placement.Top + placement.Height > SystemParameters.VirtualScreenTop &&
            placement.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (!isVisible)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.Width < MinWidth || bounds.Height < MinHeight)
        {
            return;
        }

        _viewModel.UpdateWindowPlacement(new WindowPlacementSettings(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            WindowState == WindowState.Maximized ||
                WindowState == WindowState.Minimized &&
                _lastNonMinimizedWindowState == WindowState.Maximized));
    }

    private void TranscriptTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox transcriptTextBox)
        {
            return;
        }

        UpdateSearchSummary();
        var autoScrollEnabled = DataContext is MainViewModel viewModel &&
            viewModel.IsAutoScrollEnabled;
        var targetOffset = _manualScrollOffset;

        _ = transcriptTextBox.Dispatcher.BeginInvoke(
            () =>
            {
                if (autoScrollEnabled)
                {
                    transcriptTextBox.ScrollToEnd();
                }
                else
                {
                    transcriptTextBox.ScrollToVerticalOffset(targetOffset);
                }
            },
            DispatcherPriority.Background);
    }

    private void TranscriptTextBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is MainViewModel { IsAutoScrollEnabled: false } &&
            e.ExtentHeightChange == 0)
        {
            _manualScrollOffset = e.VerticalOffset;
        }
    }

    private void TranscriptSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _lastSearchQuery = string.Empty;
        UpdateSearchSummary();
    }

    private void TranscriptSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        FindNext();
        e.Handled = true;
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e) => FindNext();

    private void AutoScrollCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (TranscriptTextBox is not null)
        {
            TranscriptTextBox.ScrollToEnd();
        }
    }

    private void AutoScrollCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (TranscriptTextBox is not null)
        {
            _manualScrollOffset = TranscriptTextBox.VerticalOffset;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.IsHistoryOpen)
        {
            _viewModel.CloseHistoryCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsSettingsOpen)
        {
            _viewModel.CloseSettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_viewModel.IsSettingsOpen || _viewModel.IsHistoryOpen)
        {
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            TranscriptSearchBox.Focus();
            TranscriptSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void FindNext()
    {
        var query = TranscriptSearchBox.Text;
        var continuesSearch = string.Equals(
            query,
            _lastSearchQuery,
            StringComparison.CurrentCultureIgnoreCase);
        var selectionStart = continuesSearch ? TranscriptTextBox.SelectionStart : 0;
        var selectionLength = continuesSearch ? TranscriptTextBox.SelectionLength : 0;
        var result = _searchService.FindNext(
            TranscriptTextBox.Text,
            query,
            selectionStart,
            selectionLength);

        _lastSearchQuery = query;
        if (result is null)
        {
            SearchResultText.Text = string.IsNullOrWhiteSpace(query)
                ? string.Empty
                : "No matches";
            return;
        }

        TranscriptTextBox.Focus();
        TranscriptTextBox.Select(result.Index, result.Length);
        var lineIndex = TranscriptTextBox.GetLineIndexFromCharacterIndex(result.Index);
        if (lineIndex >= 0)
        {
            TranscriptTextBox.ScrollToLine(lineIndex);
            _manualScrollOffset = TranscriptTextBox.VerticalOffset;
        }

        SearchResultText.Text = $"{result.MatchNumber}/{result.MatchCount}";
    }

    private void UpdateSearchSummary()
    {
        if (TranscriptSearchBox is null || SearchResultText is null || TranscriptTextBox is null)
        {
            return;
        }

        var query = TranscriptSearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResultText.Text = string.Empty;
            return;
        }

        var matchCount = _searchService.CountMatches(TranscriptTextBox.Text, query);
        SearchResultText.Text = matchCount == 1
            ? "1 match"
            : $"{matchCount} matches";
    }
}
