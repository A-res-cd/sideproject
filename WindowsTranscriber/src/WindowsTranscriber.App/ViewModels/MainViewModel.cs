using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WindowsTranscriber.App.Services;
using WindowsTranscriber.Audio.Processes;
using WindowsTranscriber.Core.Models;
using WindowsTranscriber.Data.History;
using WindowsTranscriber.Data.Recovery;
using WindowsTranscriber.Data.Settings;
using WindowsTranscriber.Transcription.Whisper;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace WindowsTranscriber.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const double MinimumTranscriptFontSize = AppSettings.MinimumFontSize;
    private const double MaximumTranscriptFontSize = AppSettings.MaximumFontSize;
    private static readonly TimeSpan RecoverySaveInterval = TimeSpan.FromSeconds(5);

    private static readonly TranscriptionLanguageOption[] AvailableLanguageOptions =
    [
        new("auto", "Auto detect"),
        new("en", "English"),
        new("tl", "Filipino / Tagalog"),
        new("es", "Spanish"),
        new("fr", "French"),
        new("de", "German"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh", "Chinese"),
    ];

    private static readonly WhisperModelOption[] AvailableModelOptions =
    [
        new(WhisperModelSize.Tiny, "Tiny (~75 MB)", "Fastest, lower accuracy"),
        new(WhisperModelSize.Base, "Base (~142 MB)", "Balanced speed and accuracy"),
        new(WhisperModelSize.Small, "Small (~466 MB)", "Slower, better accuracy"),
        new(WhisperModelSize.TinyEnglish, "Tiny English-only (~75 MB)", "Fast English-only model"),
        new(WhisperModelSize.BaseEnglish, "Base English-only (~142 MB)", "Balanced English-only model"),
        new(WhisperModelSize.SmallEnglish, "Small English-only (~466 MB)", "More accurate English-only model"),
    ];

    private static readonly TranscriptionQualityPresetOption[] AvailableQualityPresets =
    [
        new(TranscriptionQualityPreset.LowLatency, "Low latency", "Faster text with less decoding work"),
        new(TranscriptionQualityPreset.Balanced, "Balanced", "Good speed and accuracy for most audio"),
        new(TranscriptionQualityPreset.HighAccuracy, "High accuracy", "Beam search; slower but more careful"),
    ];

    private static readonly AppThemeOption[] AvailableThemeOptions =
    [
        new(AppThemeMode.System, "Use system theme"),
        new(AppThemeMode.Light, "Light"),
        new(AppThemeMode.Dark, "Dark"),
    ];

    private static readonly IReadOnlySet<string> SupportedLanguageCodes =
        AvailableLanguageOptions
            .Select(option => option.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly ProcessScanner _processScanner;
    private readonly LiveTranscriptionCoordinator _transcriptionCoordinator;
    private readonly TranscriptExportDialogService _exportDialogService;
    private readonly IClipboardService _clipboardService;
    private readonly TranscriptRecoveryStore _recoveryStore;
    private readonly ITranscriptRecoveryPrompt _recoveryPrompt;
    private readonly AppSettingsStore _settingsStore;
    private readonly WhisperModelManager _modelManager;
    private readonly TranscriptHistoryStore _historyStore;
    private readonly Stopwatch _sessionStopwatch = new();
    private readonly StringBuilder _liveTranscriptBuilder = new();
    private readonly object _recoverySaveScheduleLock = new();
    private readonly DispatcherTimer _durationTimer;

    private ApplicationProcess? _selectedApplication;
    private string _statusMessage = "Select Refresh to find running applications.";
    private string _transcriptionStatus = "Waiting for an application.";
    private string _liveTranscript = string.Empty;
    private string _sessionDuration = "00:00";
    private string _detectedLanguageDisplay = "Language: waiting";
    private double _transcriptFontSize = 18;
    private bool _isRefreshing;
    private bool _isStartingTranscription;
    private bool _isTranscribing;
    private bool _isPaused;
    private bool _isAutoScrollEnabled = true;
    private bool _isRecoveryOnlyApplication;
    private bool _recoveryDirty;
    private bool _settingsLoaded;
    private bool _isApplyingSettings;
    private bool _settingsDirty;
    private bool _isSettingsOpen;
    private bool _isHistoryOpen;
    private bool _isModelOperationActive;
    private bool _disposed;
    private int _settingsSaveGeneration;
    private CancellationTokenSource? _modelOperationCancellation;
    private CancellationTokenSource? _recoverySaveDelayCancellation;
    private Task? _modelOperationTask;
    private Guid? _excludedHistorySessionId;
    private Guid? _currentSessionId;
    private DateTimeOffset? _sessionStartedAt;
    private string? _currentProcessName;
    private TimeSpan _sessionBaseDuration;
    private TranscriptionLanguageOption _selectedLanguageOption =
        AvailableLanguageOptions[0];
    private WhisperModelOption _selectedModelOption = AvailableModelOptions[1];
    private TranscriptHistoryItemViewModel? _selectedHistorySession;
    private TranscriptionQualityPresetOption _selectedQualityPreset =
        AvailableQualityPresets[1];
    private AppThemeOption _selectedThemeOption = AvailableThemeOptions[0];
    private double _minimumConfidence = AppSettings.Default.MinimumConfidence;
    private double _maximumNoSpeechProbability =
        AppSettings.Default.MaximumNoSpeechProbability;
    private double _overlapMilliseconds = AppSettings.Default.OverlapMilliseconds;
    private bool _markUncertainSegments = AppSettings.Default.MarkUncertainSegments;
    private bool _globalHotkeyEnabled = AppSettings.Default.GlobalHotkeyEnabled;
    private bool _minimizeToTray = AppSettings.Default.MinimizeToTray;
    private bool _minimizeWhileTranscribing =
        AppSettings.Default.MinimizeWhileTranscribing;
    private bool _notificationsEnabled = AppSettings.Default.NotificationsEnabled;
    private IReadOnlyList<RecentApplication> _recentApplicationSettings = [];
    private WindowPlacementSettings? _windowPlacement;

    public MainViewModel(
        ProcessScanner processScanner,
        LiveTranscriptionCoordinator transcriptionCoordinator,
        TranscriptExportDialogService? exportDialogService = null,
        IClipboardService? clipboardService = null,
        TranscriptRecoveryStore? recoveryStore = null,
        ITranscriptRecoveryPrompt? recoveryPrompt = null,
        AppSettingsStore? settingsStore = null,
        WhisperModelManager? modelManager = null,
        TranscriptHistoryStore? historyStore = null)
    {
        _processScanner = processScanner;
        _transcriptionCoordinator = transcriptionCoordinator;
        _exportDialogService = exportDialogService ?? new TranscriptExportDialogService();
        _clipboardService = clipboardService ?? new WindowsClipboardService();
        _recoveryStore = recoveryStore ?? new TranscriptRecoveryStore();
        _recoveryPrompt = recoveryPrompt ?? new WindowsTranscriptRecoveryPrompt();
        _settingsStore = settingsStore ?? new AppSettingsStore();
        _modelManager = modelManager ?? new WhisperModelManager();
        _historyStore = historyStore ?? new TranscriptHistoryStore();
        _durationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _durationTimer.Tick += OnDurationTimerTick;

        _transcriptionCoordinator.StatusChanged += OnTranscriptionStatusChanged;
        _transcriptionCoordinator.TranscriptReceived += OnTranscriptReceived;
        _transcriptionCoordinator.Failed += OnTranscriptionFailed;
        _transcriptionCoordinator.Stopped += OnTranscriptionStopped;

        RefreshCommand = new AsyncCommand(RefreshApplicationsAsync);
        ChangeApplicationCommand = new AsyncCommand(ChangeApplicationAsync);
        StartListeningCommand = new AsyncCommand(StartListeningAsync);
        StopListeningCommand = new AsyncCommand(StopListeningAsync);
        PauseListeningCommand = new AsyncCommand(PauseListeningAsync);
        ResumeListeningCommand = new AsyncCommand(ResumeListeningAsync);
        ClearTranscriptCommand = new AsyncCommand(ClearTranscriptAsync);
        ExportTranscriptCommand = new AsyncCommand(ExportTranscriptAsync);
        CopyTranscriptCommand = new AsyncCommand(CopyTranscriptAsync);
        IncreaseFontSizeCommand = new AsyncCommand(IncreaseFontSizeAsync);
        DecreaseFontSizeCommand = new AsyncCommand(DecreaseFontSizeAsync);
        OpenSettingsCommand = new AsyncCommand(OpenSettingsAsync);
        CloseSettingsCommand = new AsyncCommand(CloseSettingsAsync);
        OpenHistoryCommand = new AsyncCommand(OpenHistoryAsync);
        CloseHistoryCommand = new AsyncCommand(CloseHistoryAsync);
        RestoreHistoryCommand = new AsyncCommand(RestoreSelectedHistoryAsync);
        DeleteHistoryCommand = new AsyncCommand(DeleteSelectedHistoryAsync);

        foreach (var option in AvailableModelOptions)
        {
            ManagedModels.Add(new WhisperModelManagerItemViewModel(
                option,
                DownloadModelAsync,
                DeleteModelAsync,
                CancelModelDownloadAsync));
        }

        RefreshModelStates();
    }

    public ObservableCollection<ApplicationProcess> Applications { get; } = [];

    public ObservableCollection<TranscriptSegment> TranscriptSegments { get; } = [];

    public ObservableCollection<WhisperModelManagerItemViewModel> ManagedModels { get; } = [];

    public ObservableCollection<TranscriptHistoryItemViewModel> HistorySessions { get; } = [];

    public ObservableCollection<RecentApplicationShortcutViewModel> RecentApplications { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand ChangeApplicationCommand { get; }

    public ICommand StartListeningCommand { get; }

    public ICommand StopListeningCommand { get; }

    public ICommand PauseListeningCommand { get; }

    public ICommand ResumeListeningCommand { get; }

    public ICommand ClearTranscriptCommand { get; }

    public ICommand ExportTranscriptCommand { get; }

    public ICommand CopyTranscriptCommand { get; }

    public ICommand IncreaseFontSizeCommand { get; }

    public ICommand DecreaseFontSizeCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand CloseSettingsCommand { get; }

    public ICommand OpenHistoryCommand { get; }

    public ICommand CloseHistoryCommand { get; }

    public ICommand RestoreHistoryCommand { get; }

    public ICommand DeleteHistoryCommand { get; }

    public IReadOnlyList<TranscriptionLanguageOption> LanguageOptions =>
        AvailableLanguageOptions;

    public IReadOnlyList<WhisperModelOption> ModelOptions => AvailableModelOptions;

    public IReadOnlyList<TranscriptionQualityPresetOption> QualityPresetOptions =>
        AvailableQualityPresets;

    public IReadOnlyList<AppThemeOption> ThemeOptions => AvailableThemeOptions;

    public ApplicationProcess? SelectedApplication
    {
        get => _selectedApplication;
        set
        {
            if (SetField(ref _selectedApplication, value))
            {
                OnPropertyChanged(nameof(SelectedProcessId));
                OnPropertyChanged(nameof(IsApplicationSelected));
                OnPropertyChanged(nameof(CanStartTranscription));

                TranscriptionStatus = value is null
                    ? "Waiting for an application."
                    : "Ready. Press Start listening to begin local transcription.";
            }
        }
    }

    public int? SelectedProcessId => SelectedApplication?.ProcessId;

    public bool IsApplicationSelected => SelectedApplication is not null;

    public bool IsStartingTranscription
    {
        get => _isStartingTranscription;
        private set
        {
            if (SetField(ref _isStartingTranscription, value))
            {
                OnPropertyChanged(nameof(IsTranscriptionActive));
                OnPropertyChanged(nameof(CanStartTranscription));
                OnPropertyChanged(nameof(CanRestoreHistorySession));
                OnPropertyChanged(nameof(CanDeleteHistorySession));
                UpdateModelManagerAvailability();
            }
        }
    }

    public bool IsTranscribing
    {
        get => _isTranscribing;
        private set
        {
            if (SetField(ref _isTranscribing, value))
            {
                OnPropertyChanged(nameof(IsTranscriptionActive));
                OnPropertyChanged(nameof(CanStartTranscription));
                OnPropertyChanged(nameof(CanRestoreHistorySession));
                OnPropertyChanged(nameof(CanDeleteHistorySession));
                UpdateModelManagerAvailability();
            }
        }
    }

    public bool IsTranscriptionActive => IsStartingTranscription || IsTranscribing;

    public bool CanStartTranscription =>
        IsApplicationSelected &&
        !IsTranscriptionActive &&
        !IsModelOperationActive &&
        !IsRecoveryOnlyApplication;

    public bool IsModelOperationActive
    {
        get => _isModelOperationActive;
        private set
        {
            if (SetField(ref _isModelOperationActive, value))
            {
                OnPropertyChanged(nameof(CanStartTranscription));
                UpdateModelManagerAvailability();
            }
        }
    }

    public bool HasTranscript => TranscriptSegments.Count > 0;

    public bool IsRecoveryOnlyApplication
    {
        get => _isRecoveryOnlyApplication;
        private set
        {
            if (SetField(ref _isRecoveryOnlyApplication, value))
            {
                OnPropertyChanged(nameof(CanStartTranscription));
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set => SetField(ref _isPaused, value);
    }

    public string SessionDuration
    {
        get => _sessionDuration;
        private set => SetField(ref _sessionDuration, value);
    }

    public double TranscriptFontSize
    {
        get => _transcriptFontSize;
        private set
        {
            if (SetField(ref _transcriptFontSize, value))
            {
                OnPropertyChanged(nameof(CanIncreaseFontSize));
                OnPropertyChanged(nameof(CanDecreaseFontSize));
                QueueSettingsSave();
            }
        }
    }

    public bool CanIncreaseFontSize => TranscriptFontSize < MaximumTranscriptFontSize;

    public bool CanDecreaseFontSize => TranscriptFontSize > MinimumTranscriptFontSize;

    public bool IsAutoScrollEnabled
    {
        get => _isAutoScrollEnabled;
        set
        {
            if (SetField(ref _isAutoScrollEnabled, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public TranscriptionLanguageOption SelectedLanguageOption
    {
        get => _selectedLanguageOption;
        set
        {
            if (value is not null && SetField(ref _selectedLanguageOption, value))
            {
                OnPropertyChanged(nameof(SettingsConfigurationSummary));
                QueueSettingsSave();
            }
        }
    }

    public WhisperModelOption SelectedModelOption
    {
        get => _selectedModelOption;
        set
        {
            if (value is not null && SetField(ref _selectedModelOption, value))
            {
                OnPropertyChanged(nameof(SettingsConfigurationSummary));
                OnPropertyChanged(nameof(IsLanguageSelectionEnabled));
                if (IsEnglishOnlyModel(value.ModelSize))
                {
                    SelectedLanguageOption = AvailableLanguageOptions.First(option =>
                        option.Code == "en");
                }

                QueueSettingsSave();
            }
        }
    }

    public bool IsLanguageSelectionEnabled =>
        !IsEnglishOnlyModel(SelectedModelOption.ModelSize);

    public TranscriptionQualityPresetOption SelectedQualityPreset
    {
        get => _selectedQualityPreset;
        set
        {
            if (value is not null && SetField(ref _selectedQualityPreset, value))
            {
                OnPropertyChanged(nameof(SettingsConfigurationSummary));
                QueueSettingsSave();
            }
        }
    }

    public double MinimumConfidence
    {
        get => _minimumConfidence;
        set
        {
            var normalized = Math.Clamp(value, 0.05, 0.95);
            if (SetField(ref _minimumConfidence, normalized))
            {
                QueueSettingsSave();
            }
        }
    }

    public double MaximumNoSpeechProbability
    {
        get => _maximumNoSpeechProbability;
        set
        {
            var normalized = Math.Clamp(value, 0.05, 0.95);
            if (SetField(ref _maximumNoSpeechProbability, normalized))
            {
                QueueSettingsSave();
            }
        }
    }

    public double OverlapMilliseconds
    {
        get => _overlapMilliseconds;
        set
        {
            var normalized = Math.Clamp(Math.Round(value / 50) * 50, 0, 2_000);
            if (SetField(ref _overlapMilliseconds, normalized))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool MarkUncertainSegments
    {
        get => _markUncertainSegments;
        set
        {
            if (SetField(ref _markUncertainSegments, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool GlobalHotkeyEnabled
    {
        get => _globalHotkeyEnabled;
        set
        {
            if (SetField(ref _globalHotkeyEnabled, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (SetField(ref _minimizeToTray, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool MinimizeWhileTranscribing
    {
        get => _minimizeWhileTranscribing;
        set
        {
            if (SetField(ref _minimizeWhileTranscribing, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (SetField(ref _notificationsEnabled, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public AppThemeOption SelectedThemeOption
    {
        get => _selectedThemeOption;
        set
        {
            if (value is not null && SetField(ref _selectedThemeOption, value))
            {
                ThemeChanged?.Invoke(value.Mode);
                QueueSettingsSave();
            }
        }
    }

    public WindowPlacementSettings? WindowPlacement => _windowPlacement;

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set
        {
            if (SetField(ref _isSettingsOpen, value))
            {
                OnPropertyChanged(nameof(IsOverlayOpen));
            }
        }
    }

    public bool IsHistoryOpen
    {
        get => _isHistoryOpen;
        private set
        {
            if (SetField(ref _isHistoryOpen, value))
            {
                OnPropertyChanged(nameof(IsOverlayOpen));
            }
        }
    }

    public bool IsOverlayOpen => IsSettingsOpen || IsHistoryOpen;

    public TranscriptHistoryItemViewModel? SelectedHistorySession
    {
        get => _selectedHistorySession;
        set
        {
            if (SetField(ref _selectedHistorySession, value))
            {
                OnPropertyChanged(nameof(CanRestoreHistorySession));
                OnPropertyChanged(nameof(CanDeleteHistorySession));
            }
        }
    }

    public bool HasHistorySessions => HistorySessions.Count > 0;

    public bool CanRestoreHistorySession =>
        SelectedHistorySession is not null && !IsTranscriptionActive;

    public bool CanDeleteHistorySession =>
        SelectedHistorySession is not null && !IsTranscriptionActive;

    public string SettingsConfigurationSummary =>
        $"{SelectedLanguageOption.DisplayName} | {SelectedModelOption.DisplayName} | " +
        SelectedQualityPreset.DisplayName;

    public string DetectedLanguageDisplay
    {
        get => _detectedLanguageDisplay;
        private set => SetField(ref _detectedLanguageDisplay, value);
    }

    public string TranscriptionStatus
    {
        get => _transcriptionStatus;
        private set => SetField(ref _transcriptionStatus, value);
    }

    public string LiveTranscript
    {
        get => _liveTranscript;
        private set => SetField(ref _liveTranscript, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetField(ref _isRefreshing, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? TranscriptionStarted;

    public event Action<string, string>? NotificationRequested;

    public event Action<AppThemeMode>? ThemeChanged;

    public async Task LoadSettingsAsync()
    {
        try
        {
            var loadResult = await _settingsStore.LoadAsync(SupportedLanguageCodes);
            _isApplyingSettings = true;
            try
            {
                TranscriptFontSize = loadResult.Settings.TranscriptFontSize;
                IsAutoScrollEnabled = loadResult.Settings.AutoScrollEnabled;
                SelectedLanguageOption = AvailableLanguageOptions.First(option =>
                    string.Equals(
                        option.Code,
                        loadResult.Settings.LanguageCode,
                        StringComparison.OrdinalIgnoreCase));
                SelectedModelOption = AvailableModelOptions.First(option =>
                    option.ModelSize == loadResult.Settings.ModelSize);
                SelectedQualityPreset = AvailableQualityPresets.First(option =>
                    option.Preset == loadResult.Settings.QualityPreset);
                MinimumConfidence = loadResult.Settings.MinimumConfidence;
                MaximumNoSpeechProbability =
                    loadResult.Settings.MaximumNoSpeechProbability;
                OverlapMilliseconds = loadResult.Settings.OverlapMilliseconds;
                MarkUncertainSegments = loadResult.Settings.MarkUncertainSegments;
                GlobalHotkeyEnabled = loadResult.Settings.GlobalHotkeyEnabled;
                MinimizeToTray = loadResult.Settings.MinimizeToTray;
                MinimizeWhileTranscribing =
                    loadResult.Settings.MinimizeWhileTranscribing;
                NotificationsEnabled = loadResult.Settings.NotificationsEnabled;
                SelectedThemeOption = AvailableThemeOptions.First(option =>
                    option.Mode == loadResult.Settings.ThemeMode);
                _recentApplicationSettings = loadResult.Settings.RecentApplications;
                _windowPlacement = loadResult.Settings.WindowPlacement;
                RebuildRecentApplicationShortcuts();
            }
            finally
            {
                _isApplyingSettings = false;
                _settingsLoaded = true;
            }

            if (loadResult.WasCorrupted)
            {
                StatusMessage =
                    "Damaged settings were reset. The old file was preserved as " +
                    Path.GetFileName(loadResult.QuarantinedFilePath) + ".";
            }
        }
        catch (Exception exception)
        {
            _settingsLoaded = true;
            StatusMessage =
                $"Unable to load settings: {exception.GetBaseException().Message}";
        }
    }

    public async Task ToggleListeningFromHotkeyAsync()
    {
        if (IsTranscriptionActive || _transcriptionCoordinator.IsRunning)
        {
            await StopListeningAsync();
            return;
        }

        if (CanStartTranscription)
        {
            await StartListeningAsync();
            return;
        }

        NotificationRequested?.Invoke(
            "WindowsTranscriber",
            "Select a running application before using the start hotkey.");
    }

    public void UpdateWindowPlacement(WindowPlacementSettings placement)
    {
        _windowPlacement = placement;
        QueueSettingsSave();
    }

    public async Task LoadHistoryAsync()
    {
        try
        {
            var loadResult = await _historyStore.LoadAsync();
            ReplaceHistorySessions(loadResult.Sessions);

            if (loadResult.WasCorrupted)
            {
                StatusMessage =
                    "Damaged transcript history was preserved as " +
                    Path.GetFileName(loadResult.QuarantinedFilePath) + ".";
            }
        }
        catch (Exception exception)
        {
            StatusMessage =
                $"Unable to load transcript history: {exception.GetBaseException().Message}";
        }
    }

    public async Task RefreshApplicationsAsync()
    {
        IsRefreshing = true;
        StatusMessage = "Scanning running applications...";

        try
        {
            var selectedProcessId = SelectedProcessId;
            var applications = await Task.Run(_processScanner.GetRunningApplications);

            Applications.Clear();
            foreach (var application in applications)
            {
                Applications.Add(application);
            }

            SelectedApplication = Applications.FirstOrDefault(
                application => application.ProcessId == selectedProcessId);

            StatusMessage = Applications.Count == 0
                ? "No user applications with visible windows were found."
                : $"Found {Applications.Count} running application(s).";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Unable to scan applications: {exception.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task RestoreRecoveryIfAvailableAsync()
    {
        TranscriptRecoveryLoadResult loadResult;
        try
        {
            loadResult = await _recoveryStore.LoadAsync();
        }
        catch (Exception exception)
        {
            StatusMessage =
                $"Unable to check transcript recovery: {exception.GetBaseException().Message}";
            return;
        }

        if (loadResult.WasCorrupted)
        {
            StatusMessage =
                "A damaged recovery file was preserved for troubleshooting: " +
                Path.GetFileName(loadResult.QuarantinedFilePath);
            return;
        }

        var session = loadResult.Session;
        if (session is null)
        {
            return;
        }

        if (session.Segments.Count == 0)
        {
            await DeleteRecoveryFileAsync();
            return;
        }

        if (!_recoveryPrompt.ShouldRestore(session))
        {
            await DeleteRecoveryFileAsync();
            StatusMessage = "Autosaved transcript discarded.";
            return;
        }

        RestoreSession(session);
        QueueHistorySave();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _modelOperationCancellation?.Cancel();
        if (_modelOperationTask is not null)
        {
            try
            {
                await _modelOperationTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        StopSessionTimer();
        _durationTimer.Tick -= OnDurationTimerTick;

        await _transcriptionCoordinator.DisposeAsync();
        await SaveRecoveryNowAsync();
        await SaveHistoryNowAsync();
        await SaveSettingsNowAsync();

        _transcriptionCoordinator.StatusChanged -= OnTranscriptionStatusChanged;
        _transcriptionCoordinator.TranscriptReceived -= OnTranscriptReceived;
        _transcriptionCoordinator.Failed -= OnTranscriptionFailed;
        _transcriptionCoordinator.Stopped -= OnTranscriptionStopped;
    }

    private async Task StartListeningAsync()
    {
        var processId = SelectedProcessId;
        if (processId is null || IsTranscriptionActive || IsModelOperationActive)
        {
            return;
        }

        if (HasTranscript && !ConfirmTranscriptDiscard(
            "Starting a new session will clear the current transcript."))
        {
            return;
        }

        await SaveHistoryNowAsync();
        await DeleteRecoveryFileAsync();
        _recoveryDirty = false;
        ClearTranscript();
        ResetSessionTimer();
        IsRecoveryOnlyApplication = false;
        IsPaused = false;
        IsStartingTranscription = true;
        TranscriptionStatus = "Starting local transcription...";
        var effectiveLanguageCode = IsEnglishOnlyModel(SelectedModelOption.ModelSize)
            ? "en"
            : SelectedLanguageOption.Code;
        DetectedLanguageDisplay = effectiveLanguageCode == "auto"
            ? "Language: detecting..."
            : $"Language: {GetLanguageDisplayName(effectiveLanguageCode)}";

        try
        {
            var applicationName = SelectedApplication?.DisplayName ?? "Unknown application";
            var sessionId = Guid.NewGuid();
            _currentSessionId = sessionId;
            _excludedHistorySessionId = null;
            _sessionStartedAt = DateTimeOffset.Now;
            _currentProcessName = SelectedApplication?.ProcessName ?? "Unknown";
            await _transcriptionCoordinator.StartAsync(
                processId.Value,
                applicationName,
                sessionId,
                SelectedModelOption.ModelSize,
                effectiveLanguageCode,
                CreateQualityOptions());
            IsTranscribing = _transcriptionCoordinator.IsRunning;
            if (IsTranscribing)
            {
                _sessionStopwatch.Start();
                _durationTimer.Start();
                RememberRecentApplication(
                    SelectedApplication?.ProcessName ?? "Unknown",
                    applicationName);
                TranscriptionStarted?.Invoke();
                Notify(
                    "Transcription started",
                    $"Listening to {applicationName}.");
            }
        }
        catch (OperationCanceledException)
        {
            TranscriptionStatus = "Transcription start was canceled.";
        }
        catch (Exception exception)
        {
            TranscriptionStatus =
                $"Unable to start transcription: {exception.GetBaseException().Message}";
            Notify("Unable to start transcription", exception.GetBaseException().Message);
        }
        finally
        {
            IsStartingTranscription = false;
            await RefreshModelStatesAsync();
        }
    }

    private async Task StopListeningAsync()
    {
        var wasActive = IsTranscriptionActive || _transcriptionCoordinator.IsRunning;
        if (!wasActive)
        {
            return;
        }

        TranscriptionStatus = "Stopping transcription...";

        try
        {
            await _transcriptionCoordinator.StopAsync();
        }
        catch (Exception exception)
        {
            TranscriptionStatus =
                $"Unable to stop transcription cleanly: {exception.GetBaseException().Message}";
        }
        finally
        {
            StopSessionTimer();
            IsPaused = false;
            IsStartingTranscription = false;
            IsTranscribing = false;
            QueueRecoverySave();
            QueueHistorySave();
            Notify(
                "Transcription stopped",
                HasTranscript
                    ? "The current transcript was saved to History."
                    : "Listening stopped before speech was captured.");
        }
    }

    private Task PauseListeningAsync()
    {
        if (IsTranscribing && !IsPaused && _transcriptionCoordinator.Pause())
        {
            IsPaused = true;
            _sessionStopwatch.Stop();
            UpdateSessionDuration();
            QueueRecoverySave();
        }

        return Task.CompletedTask;
    }

    private Task ResumeListeningAsync()
    {
        if (IsTranscribing && IsPaused && _transcriptionCoordinator.Resume())
        {
            IsPaused = false;
            _sessionStopwatch.Start();
        }

        return Task.CompletedTask;
    }

    private async Task ChangeApplicationAsync()
    {
        if (HasTranscript && !ConfirmTranscriptDiscard(
            "Changing applications will clear the current transcript."))
        {
            return;
        }

        await StopListeningAsync();
        await SaveHistoryNowAsync();
        await DeleteRecoveryFileAsync();
        _recoveryDirty = false;
        SelectedApplication = null;
        IsRecoveryOnlyApplication = false;
        ClearTranscript();
        ResetSessionTimer();
        ClearSessionIdentity();
        StatusMessage = "Select an application to continue.";
    }

    private async Task ClearTranscriptAsync()
    {
        if (!HasTranscript || !ConfirmTranscriptDiscard(
            "This will clear the transcript from the current view."))
        {
            return;
        }

        await SaveHistoryNowAsync();
        ClearTranscript();
        _recoveryDirty = false;
        await DeleteRecoveryFileAsync();
        if (!IsTranscriptionActive)
        {
            ResetSessionTimer();
            ClearSessionIdentity();
        }

        TranscriptionStatus = IsTranscriptionActive
            ? IsPaused
                ? "Transcript cleared. Transcription remains paused."
                : "Transcript cleared. Listening for more audio..."
            : "Transcript cleared.";
    }

    private async Task ExportTranscriptAsync()
    {
        if (!HasTranscript)
        {
            return;
        }

        try
        {
            var segments = TranscriptSegments.ToArray();
            var applicationName = SelectedApplication?.DisplayName ??
                segments[0].ApplicationName;
            var exportedFile = await _exportDialogService.ExportAsync(
                segments,
                applicationName);

            if (exportedFile is not null)
            {
                await SaveHistoryNowAsync();
                _recoveryDirty = false;
                TranscriptionStatus =
                    $"Transcript saved: {Path.GetFileName(exportedFile)}";
                await DeleteRecoveryFileAsync();
            }
        }
        catch (Exception exception)
        {
            TranscriptionStatus =
                $"Unable to save transcript: {exception.GetBaseException().Message}";
        }
    }

    private Task CopyTranscriptAsync()
    {
        if (!HasTranscript)
        {
            return Task.CompletedTask;
        }

        try
        {
            _clipboardService.SetText(LiveTranscript);
            TranscriptionStatus = "Transcript copied to clipboard.";
        }
        catch (Exception exception)
        {
            TranscriptionStatus =
                $"Unable to copy transcript: {exception.GetBaseException().Message}";
        }

        return Task.CompletedTask;
    }

    private Task IncreaseFontSizeAsync()
    {
        TranscriptFontSize = Math.Min(
            MaximumTranscriptFontSize,
            TranscriptFontSize + 2);
        return Task.CompletedTask;
    }

    private Task DecreaseFontSizeAsync()
    {
        TranscriptFontSize = Math.Max(
            MinimumTranscriptFontSize,
            TranscriptFontSize - 2);
        return Task.CompletedTask;
    }

    private async Task OpenSettingsAsync()
    {
        IsHistoryOpen = false;
        IsSettingsOpen = true;
        await Dispatcher.Yield(DispatcherPriority.Background);
        await RefreshModelStatesAsync();
    }

    private async Task CloseSettingsAsync()
    {
        IsSettingsOpen = false;
        await SaveSettingsNowAsync();
    }

    private async Task OpenHistoryAsync()
    {
        IsSettingsOpen = false;
        IsHistoryOpen = true;
        await Dispatcher.Yield(DispatcherPriority.Background);
        await SaveHistoryNowAsync();
        await LoadHistoryAsync();
    }

    private Task CloseHistoryAsync()
    {
        IsHistoryOpen = false;
        return Task.CompletedTask;
    }

    private async Task RestoreSelectedHistoryAsync()
    {
        var item = SelectedHistorySession;
        if (item is null || IsTranscriptionActive)
        {
            return;
        }

        if (HasTranscript && _currentSessionId != item.SessionId)
        {
            var answer = MessageBox.Show(
                "Open this saved transcript and replace the current view?" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "The current transcript remains saved in History.",
                "Open saved transcript?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await SaveHistoryNowAsync();
        await DeleteRecoveryFileAsync();
        RestoreHistorySession(item.Session);
        IsHistoryOpen = false;
    }

    private async Task DeleteSelectedHistoryAsync()
    {
        var item = SelectedHistorySession;
        if (item is null || IsTranscriptionActive)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Delete the saved transcript from {item.ApplicationName}?" +
            $"{Environment.NewLine}{item.StartedAtText}" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "This cannot be undone.",
            "Delete saved transcript?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _historyStore.DeleteAsync(item.SessionId);
            HistorySessions.Remove(item);
            SelectedHistorySession = null;
            OnPropertyChanged(nameof(HasHistorySessions));

            if (_currentSessionId == item.SessionId)
            {
                _excludedHistorySessionId = item.SessionId;
                _recoveryDirty = false;
                await DeleteRecoveryFileAsync();
            }
        }
        catch (Exception exception)
        {
            TranscriptionStatus =
                $"Unable to delete history: {exception.GetBaseException().Message}";
        }
    }

    private async Task DownloadModelAsync(WhisperModelManagerItemViewModel model)
    {
        if (IsTranscriptionActive || IsModelOperationActive)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _modelOperationCancellation = cancellation;
        model.BeginDownload();
        IsModelOperationActive = true;

        var progress = new Progress<WhisperModelDownloadProgress>(model.ReportProgress);
        var operation = _modelManager.DownloadAsync(
            model.ModelSize,
            progress,
            cancellation.Token);
        _modelOperationTask = operation;

        try
        {
            await operation;
            model.ApplyState(_modelManager.GetState(model.ModelSize));
            TranscriptionStatus = $"Whisper {model.DisplayName} model is ready.";
        }
        catch (OperationCanceledException)
        {
            model.ApplyState(_modelManager.GetState(model.ModelSize));
            TranscriptionStatus = $"Whisper {model.DisplayName} download canceled.";
        }
        catch (Exception exception)
        {
            model.SetDownloadError(exception.GetBaseException().Message);
            TranscriptionStatus =
                $"Model download failed: {exception.GetBaseException().Message}";
        }
        finally
        {
            await RefreshModelStatesAsync(model.HasDownloadError ? model : null);
            _modelOperationTask = null;
            _modelOperationCancellation = null;
            cancellation.Dispose();
            IsModelOperationActive = false;
        }
    }

    private async Task DeleteModelAsync(WhisperModelManagerItemViewModel model)
    {
        if (IsTranscriptionActive || IsModelOperationActive || !model.IsInstalled)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Delete the Whisper {model.DisplayName} model?" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "It must download again before you can use it.",
            "Delete model?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _modelOperationCancellation = cancellation;
        IsModelOperationActive = true;
        var operation = DeleteModelFilesAsync(model.ModelSize, cancellation.Token);
        _modelOperationTask = operation;

        try
        {
            await operation;
            await RefreshModelStatesAsync();
            TranscriptionStatus = $"Whisper {model.DisplayName} model deleted.";
        }
        catch (Exception exception)
        {
            model.SetDownloadError(exception.GetBaseException().Message);
            TranscriptionStatus =
                $"Unable to delete model: {exception.GetBaseException().Message}";
        }
        finally
        {
            _modelOperationTask = null;
            _modelOperationCancellation = null;
            cancellation.Dispose();
            IsModelOperationActive = false;
        }
    }

    private Task CancelModelDownloadAsync(WhisperModelManagerItemViewModel model)
    {
        if (model.IsDownloading)
        {
            _modelOperationCancellation?.Cancel();
        }

        return Task.CompletedTask;
    }

    private async Task DeleteModelFilesAsync(
        WhisperModelSize modelSize,
        CancellationToken cancellationToken)
    {
        await _transcriptionCoordinator.UnloadModelAsync(cancellationToken);
        await _modelManager.DeleteAsync(modelSize, cancellationToken);
    }

    private void RefreshModelStates(WhisperModelManagerItemViewModel? except = null)
    {
        foreach (var model in ManagedModels)
        {
            if (!ReferenceEquals(model, except))
            {
                model.ApplyState(_modelManager.GetState(model.ModelSize));
            }
        }

        UpdateModelManagerAvailability();
    }

    private async Task RefreshModelStatesAsync(
        WhisperModelManagerItemViewModel? except = null)
    {
        var models = ManagedModels
            .Where(model => !ReferenceEquals(model, except))
            .ToArray();
        var modelSizes = models.Select(model => model.ModelSize).ToArray();
        var states = await Task.Run(() =>
            modelSizes.Select(_modelManager.GetState).ToArray());

        for (var index = 0; index < models.Length; index++)
        {
            models[index].ApplyState(states[index]);
        }

        UpdateModelManagerAvailability();
    }

    private void UpdateModelManagerAvailability()
    {
        foreach (var model in ManagedModels)
        {
            model.SetManagerBusy(IsModelOperationActive);
            model.SetTranscriptionActive(IsTranscriptionActive);
        }
    }

    private void OnTranscriptionStatusChanged(string status) =>
        RunOnUiThread(() => TranscriptionStatus = status);

    private void OnTranscriptReceived(TranscriptSegment segment) =>
        RunOnUiThread(() =>
        {
            _currentSessionId ??= segment.SessionId;
            _sessionStartedAt ??= DateTimeOffset.Now - segment.End;
            _currentProcessName ??= SelectedApplication?.ProcessName ?? "Unknown";
            TranscriptSegments.Add(segment);
            OnPropertyChanged(nameof(HasTranscript));
            if (!string.IsNullOrWhiteSpace(segment.LanguageCode))
            {
                DetectedLanguageDisplay =
                    $"Language: {GetLanguageDisplayName(segment.LanguageCode)}";
            }

            var timestampedText = FormatSegmentLine(segment);
            if (_liveTranscriptBuilder.Length > 0)
            {
                _liveTranscriptBuilder.AppendLine();
            }

            _liveTranscriptBuilder.Append(timestampedText);
            LiveTranscript = _liveTranscriptBuilder.ToString();
            _recoveryDirty = true;
            QueueRecoverySave();
        });

    private void OnTranscriptionFailed(Exception exception) =>
        RunOnUiThread(() =>
        {
            IsStartingTranscription = false;
            IsTranscribing = false;
            IsPaused = false;
            StopSessionTimer();
            QueueRecoverySave();
            QueueHistorySave();
            TranscriptionStatus =
                $"Transcription stopped: {exception.GetBaseException().Message}";
            Notify(
                "Transcription stopped unexpectedly",
                exception.GetBaseException().Message);
        });

    private void OnTranscriptionStopped() =>
        RunOnUiThread(() =>
        {
            IsStartingTranscription = false;
            IsTranscribing = false;
            IsPaused = false;
            StopSessionTimer();
            QueueRecoverySave();
            QueueHistorySave();
        });

    private void OnDurationTimerTick(object? sender, EventArgs e) =>
        UpdateSessionDuration();

    private void ClearTranscript()
    {
        TranscriptSegments.Clear();
        _liveTranscriptBuilder.Clear();
        LiveTranscript = string.Empty;
        DetectedLanguageDisplay = "Language: waiting";
        OnPropertyChanged(nameof(HasTranscript));
    }

    private void RestoreSession(TranscriptRecoverySession session)
    {
        ClearTranscript();

        var runningApplication = Applications.FirstOrDefault(application =>
            application.ProcessId == session.ProcessId &&
            string.Equals(
                application.ProcessName,
                session.ProcessName,
                StringComparison.OrdinalIgnoreCase));
        SelectedApplication = runningApplication ?? new ApplicationProcess(
            session.ProcessId,
            session.ProcessName,
            session.ApplicationName,
            "Recovered autosave");
        IsRecoveryOnlyApplication = runningApplication is null;

        foreach (var segment in session.Segments)
        {
            TranscriptSegments.Add(segment);
        }

        _liveTranscriptBuilder.Append(string.Join(
            Environment.NewLine,
            session.Segments.Select(FormatSegmentLine)));
        LiveTranscript = _liveTranscriptBuilder.ToString();
        OnPropertyChanged(nameof(HasTranscript));

        _currentSessionId = session.SessionId;
        _sessionStartedAt = session.StartedAt;
        _currentProcessName = session.ProcessName;
        _sessionBaseDuration = session.ActiveDuration;
        _sessionStopwatch.Reset();
        _durationTimer.Stop();
        UpdateSessionDuration();
        IsStartingTranscription = false;
        IsTranscribing = false;
        IsPaused = false;
        _recoveryDirty = true;
        UpdateDetectedLanguage(session.Segments);

        TranscriptionStatus =
            $"Recovered autosave from {session.LastSavedAt.ToLocalTime():g}. " +
            "Not currently listening.";
        StatusMessage = "Autosaved transcript restored.";
    }

    private void RestoreHistorySession(TranscriptHistorySession session)
    {
        ClearTranscript();

        var runningApplication = Applications.FirstOrDefault(application =>
            application.ProcessId == session.ProcessId &&
            string.Equals(
                application.ProcessName,
                session.ProcessName,
                StringComparison.OrdinalIgnoreCase));
        SelectedApplication = runningApplication ?? new ApplicationProcess(
            session.ProcessId,
            session.ProcessName,
            session.ApplicationName,
            "Saved transcript");
        IsRecoveryOnlyApplication = runningApplication is null;

        foreach (var segment in session.Segments)
        {
            TranscriptSegments.Add(segment);
        }

        _liveTranscriptBuilder.Append(string.Join(
            Environment.NewLine,
            session.Segments.Select(FormatSegmentLine)));
        LiveTranscript = _liveTranscriptBuilder.ToString();
        OnPropertyChanged(nameof(HasTranscript));

        _currentSessionId = session.SessionId;
        _excludedHistorySessionId = null;
        _sessionStartedAt = session.StartedAt;
        _currentProcessName = session.ProcessName;
        _sessionBaseDuration = session.ActiveDuration;
        _sessionStopwatch.Reset();
        _durationTimer.Stop();
        UpdateSessionDuration();
        IsStartingTranscription = false;
        IsTranscribing = false;
        IsPaused = false;
        _recoveryDirty = false;
        UpdateDetectedLanguage(session.Segments);

        TranscriptionStatus =
            $"Opened saved transcript from {session.StartedAt.ToLocalTime():g}.";
        StatusMessage = "Saved transcript opened.";
    }

    private TranscriptRecoverySession? CreateRecoverySnapshot()
    {
        if (!_recoveryDirty || !HasTranscript)
        {
            return null;
        }

        var firstSegment = TranscriptSegments[0];
        return new TranscriptRecoverySession(
            TranscriptRecoverySession.CurrentSchemaVersion,
            _currentSessionId ?? firstSegment.SessionId,
            firstSegment.ProcessId,
            _currentProcessName ?? SelectedApplication?.ProcessName ?? "Unknown",
            firstSegment.ApplicationName,
            _sessionStartedAt ?? DateTimeOffset.Now - firstSegment.End,
            _sessionBaseDuration + _sessionStopwatch.Elapsed,
            DateTimeOffset.Now,
            TranscriptSegments.ToArray());
    }

    private TranscriptHistorySession? CreateHistorySnapshot()
    {
        if (!HasTranscript)
        {
            return null;
        }

        var firstSegment = TranscriptSegments[0];
        var sessionId = _currentSessionId ?? firstSegment.SessionId;
        if (_excludedHistorySessionId == sessionId)
        {
            return null;
        }

        return new TranscriptHistorySession(
            TranscriptHistorySession.CurrentSchemaVersion,
            sessionId,
            firstSegment.ProcessId,
            _currentProcessName ?? SelectedApplication?.ProcessName ?? "Unknown",
            firstSegment.ApplicationName,
            _sessionStartedAt ?? DateTimeOffset.Now - firstSegment.End,
            _sessionBaseDuration + _sessionStopwatch.Elapsed,
            DateTimeOffset.Now,
            TranscriptSegments.ToArray());
    }

    private void QueueRecoverySave()
    {
        if (!_recoveryDirty || !HasTranscript)
        {
            return;
        }

        CancellationTokenSource delayCancellation;
        lock (_recoverySaveScheduleLock)
        {
            if (_recoverySaveDelayCancellation is not null)
            {
                return;
            }

            delayCancellation = new CancellationTokenSource();
            _recoverySaveDelayCancellation = delayCancellation;
        }

        _ = SaveRecoveryAfterDelayAsync(delayCancellation);
    }

    private async Task SaveRecoveryAfterDelayAsync(
        CancellationTokenSource delayCancellation)
    {
        try
        {
            await Task.Delay(
                RecoverySaveInterval,
                delayCancellation.Token);
            ReleaseRecoverySaveDelay(delayCancellation);

            var snapshot = CreateRecoverySnapshot();
            if (snapshot is not null)
            {
                await SaveRecoverySnapshotAsync(snapshot).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            ReleaseRecoverySaveDelay(delayCancellation);
            delayCancellation.Dispose();
        }
    }

    private async Task SaveRecoveryNowAsync()
    {
        CancelPendingRecoverySave();
        var snapshot = CreateRecoverySnapshot();
        if (snapshot is not null)
        {
            await SaveRecoverySnapshotAsync(snapshot);
        }
    }

    private async Task SaveRecoverySnapshotAsync(TranscriptRecoverySession snapshot)
    {
        try
        {
            await _recoveryStore.SaveAsync(snapshot);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => TranscriptionStatus =
                $"Autosave failed: {exception.GetBaseException().Message}");
        }
    }

    private void QueueHistorySave()
    {
        var snapshot = CreateHistorySnapshot();
        if (snapshot is not null)
        {
            _ = SaveHistorySnapshotAsync(snapshot);
        }
    }

    private async Task SaveHistoryNowAsync()
    {
        var snapshot = CreateHistorySnapshot();
        if (snapshot is not null)
        {
            await SaveHistorySnapshotAsync(snapshot);
        }
    }

    private async Task SaveHistorySnapshotAsync(TranscriptHistorySession snapshot)
    {
        try
        {
            await _historyStore.UpsertAsync(snapshot);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => TranscriptionStatus =
                $"History save failed: {exception.GetBaseException().Message}");
        }
    }

    private void ReplaceHistorySessions(
        IReadOnlyList<TranscriptHistorySession> sessions)
    {
        HistorySessions.Clear();
        foreach (var session in sessions)
        {
            HistorySessions.Add(new TranscriptHistoryItemViewModel(session));
        }

        SelectedHistorySession = null;
        OnPropertyChanged(nameof(HasHistorySessions));
    }

    private async Task DeleteRecoveryFileAsync()
    {
        CancelPendingRecoverySave();
        try
        {
            await _recoveryStore.DeleteAsync();
        }
        catch (Exception exception)
        {
            TranscriptionStatus =
                $"Unable to remove recovery file: {exception.GetBaseException().Message}";
        }
    }

    private void CancelPendingRecoverySave()
    {
        lock (_recoverySaveScheduleLock)
        {
            var delayCancellation = _recoverySaveDelayCancellation;
            _recoverySaveDelayCancellation = null;
            delayCancellation?.Cancel();
        }
    }

    private void ReleaseRecoverySaveDelay(
        CancellationTokenSource delayCancellation)
    {
        lock (_recoverySaveScheduleLock)
        {
            if (ReferenceEquals(
                _recoverySaveDelayCancellation,
                delayCancellation))
            {
                _recoverySaveDelayCancellation = null;
            }
        }
    }

    private AppSettings CreateSettingsSnapshot() => new(
        AppSettings.CurrentSchemaVersion,
        TranscriptFontSize,
        IsAutoScrollEnabled,
        SelectedLanguageOption.Code,
        SelectedModelOption.ModelSize,
        SelectedQualityPreset.Preset,
        MinimumConfidence,
        MaximumNoSpeechProbability,
        (int)OverlapMilliseconds,
        MarkUncertainSegments,
        GlobalHotkeyEnabled,
        MinimizeToTray,
        MinimizeWhileTranscribing,
        NotificationsEnabled,
        SelectedThemeOption.Mode,
        _recentApplicationSettings,
        _windowPlacement);

    private TranscriptionQualityOptions CreateQualityOptions() => new(
        SelectedQualityPreset.Preset,
        (float)MinimumConfidence,
        (float)MaximumNoSpeechProbability,
        (int)OverlapMilliseconds,
        MarkUncertainSegments);

    private void RememberRecentApplication(string processName, string displayName)
    {
        if (string.IsNullOrWhiteSpace(processName) ||
            string.Equals(processName, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _recentApplicationSettings = _recentApplicationSettings
            .Where(item => !string.Equals(
                item.ProcessName,
                processName,
                StringComparison.OrdinalIgnoreCase))
            .Prepend(new RecentApplication(processName, displayName, DateTimeOffset.Now))
            .Take(5)
            .ToArray();
        RebuildRecentApplicationShortcuts();
        QueueSettingsSave();
    }

    private void RebuildRecentApplicationShortcuts()
    {
        RecentApplications.Clear();
        foreach (var recentApplication in _recentApplicationSettings)
        {
            RecentApplications.Add(new RecentApplicationShortcutViewModel(
                recentApplication,
                SelectRecentApplicationAsync));
        }

        OnPropertyChanged(nameof(HasRecentApplications));
    }

    private async Task SelectRecentApplicationAsync(
        RecentApplicationShortcutViewModel recentApplication)
    {
        if (IsTranscriptionActive)
        {
            return;
        }

        var match = Applications.FirstOrDefault(application => string.Equals(
            application.ProcessName,
            recentApplication.ProcessName,
            StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            await RefreshApplicationsAsync();
            match = Applications.FirstOrDefault(application => string.Equals(
                application.ProcessName,
                recentApplication.ProcessName,
                StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
        {
            StatusMessage = $"{recentApplication.DisplayName} is not currently running.";
            return;
        }

        SelectedApplication = match;
    }

    public bool HasRecentApplications => RecentApplications.Count > 0;

    private void QueueSettingsSave()
    {
        if (!_settingsLoaded || _isApplyingSettings)
        {
            return;
        }

        _settingsDirty = true;
        var generation = Interlocked.Increment(ref _settingsSaveGeneration);
        var snapshot = CreateSettingsSnapshot();
        _ = SaveSettingsAfterDelayAsync(generation, snapshot);
    }

    private async Task SaveSettingsAfterDelayAsync(
        int generation,
        AppSettings snapshot)
    {
        await Task.Delay(350).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _settingsSaveGeneration))
        {
            return;
        }

        var saved = await SaveSettingsSnapshotAsync(snapshot).ConfigureAwait(false);
        if (saved && generation == Volatile.Read(ref _settingsSaveGeneration))
        {
            _settingsDirty = false;
        }
    }

    private async Task SaveSettingsNowAsync()
    {
        var generation = Interlocked.Increment(ref _settingsSaveGeneration);
        if (_settingsLoaded && _settingsDirty)
        {
            var saved = await SaveSettingsSnapshotAsync(CreateSettingsSnapshot());
            if (saved && generation == Volatile.Read(ref _settingsSaveGeneration))
            {
                _settingsDirty = false;
            }
        }
    }

    private async Task<bool> SaveSettingsSnapshotAsync(AppSettings settings)
    {
        try
        {
            await _settingsStore.SaveAsync(settings);
            return true;
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                var message =
                    $"Unable to save settings: {exception.GetBaseException().Message}";
                if (IsApplicationSelected)
                {
                    TranscriptionStatus = message;
                }
                else
                {
                    StatusMessage = message;
                }
            });
            return false;
        }
    }

    private void ClearSessionIdentity()
    {
        _currentSessionId = null;
        _sessionStartedAt = null;
        _currentProcessName = null;
    }

    private static bool ConfirmTranscriptDiscard(string message) =>
        MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}" +
            "A saved copy will remain in History.",
            "Clear current transcript?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private void StopSessionTimer()
    {
        _sessionStopwatch.Stop();
        _durationTimer.Stop();
        UpdateSessionDuration();
    }

    private void ResetSessionTimer()
    {
        _durationTimer.Stop();
        _sessionStopwatch.Reset();
        _sessionBaseDuration = TimeSpan.Zero;
        UpdateSessionDuration();
    }

    private void UpdateSessionDuration()
    {
        var elapsed = _sessionBaseDuration + _sessionStopwatch.Elapsed;
        SessionDuration = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }

    private static string FormatTimestamp(TimeSpan timestamp)
    {
        if (timestamp.TotalHours >= 1)
        {
            return $"{(int)timestamp.TotalHours:00}:{timestamp.Minutes:00}:{timestamp.Seconds:00}";
        }

        return $"{timestamp.Minutes:00}:{timestamp.Seconds:00}";
    }

    private static string FormatSegmentLine(TranscriptSegment segment) =>
        $"[{FormatTimestamp(segment.Start)}] " +
        (segment.IsUncertain ? "[uncertain] " : string.Empty) +
        segment.Text;

    private void UpdateDetectedLanguage(IReadOnlyList<TranscriptSegment> segments)
    {
        var languageCode = segments
            .Select(segment => segment.LanguageCode)
            .LastOrDefault(code => !string.IsNullOrWhiteSpace(code));
        DetectedLanguageDisplay = string.IsNullOrWhiteSpace(languageCode)
            ? "Language: unavailable"
            : $"Language: {GetLanguageDisplayName(languageCode)}";
    }

    private static string GetLanguageDisplayName(string languageCode)
    {
        if (string.Equals(languageCode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "Auto detect";
        }

        try
        {
            return CultureInfo.GetCultureInfo(languageCode).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return languageCode.ToUpperInvariant();
        }
    }

    private static bool IsEnglishOnlyModel(WhisperModelSize modelSize) =>
        modelSize is WhisperModelSize.TinyEnglish or
            WhisperModelSize.BaseEnglish or
            WhisperModelSize.SmallEnglish;

    private void Notify(string title, string message)
    {
        if (NotificationsEnabled)
        {
            NotificationRequested?.Invoke(title, message);
        }
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
