using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WindowsTranscriber.Core.Models;
using WindowsTranscriber.Transcription.Whisper;

namespace WindowsTranscriber.App.ViewModels;

public sealed class WhisperModelManagerItemViewModel : INotifyPropertyChanged
{
    private readonly WhisperModelOption _option;
    private bool _isInstalled;
    private long _installedBytes;
    private long _expectedBytes;
    private bool _isDownloading;
    private long _downloadedBytes;
    private double _downloadProgress;
    private bool _hasDownloadError;
    private string _errorMessage = string.Empty;
    private bool _isManagerBusy;
    private bool _isTranscriptionActive;

    public WhisperModelManagerItemViewModel(
        WhisperModelOption option,
        Func<WhisperModelManagerItemViewModel, Task> download,
        Func<WhisperModelManagerItemViewModel, Task> delete,
        Func<WhisperModelManagerItemViewModel, Task> cancel)
    {
        _option = option;
        DownloadCommand = new AsyncCommand(() => download(this));
        DeleteCommand = new AsyncCommand(() => delete(this));
        CancelCommand = new AsyncCommand(() => cancel(this));
    }

    public WhisperModelSize ModelSize => _option.ModelSize;

    public string DisplayName => _option.DisplayName;

    public string Description => _option.Description;

    public bool IsInstalled => _isInstalled;

    public bool IsDownloading => _isDownloading;

    public bool HasDownloadError => _hasDownloadError;

    public double DownloadProgress => _downloadProgress;

    public string DownloadActionText => HasDownloadError ? "Retry" : "Download";

    public string StatusText
    {
        get
        {
            if (IsDownloading)
            {
                return $"Downloading {DownloadProgress:0}% · " +
                    $"{FormatBytes(_downloadedBytes)} / ~{FormatBytes(_expectedBytes)}";
            }

            if (HasDownloadError)
            {
                return $"Download failed · {_errorMessage}";
            }

            return IsInstalled
                ? $"Installed · {FormatBytes(_installedBytes)} on disk"
                : $"Not installed · ~{FormatBytes(_expectedBytes)} download";
        }
    }

    public bool CanDownload =>
        !IsInstalled && !IsDownloading && !_isManagerBusy && !_isTranscriptionActive;

    public bool CanDelete =>
        IsInstalled && !IsDownloading && !_isManagerBusy && !_isTranscriptionActive;

    public bool CanCancelDownload => IsDownloading;

    public ICommand DownloadCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand CancelCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyState(WhisperModelState state)
    {
        _isInstalled = state.IsInstalled;
        _installedBytes = state.InstalledBytes;
        _expectedBytes = state.ExpectedBytes;
        _isDownloading = false;
        _downloadedBytes = 0;
        _downloadProgress = 0;
        _hasDownloadError = false;
        _errorMessage = string.Empty;
        NotifyStateChanged();
    }

    public void BeginDownload()
    {
        _isDownloading = true;
        _downloadedBytes = 0;
        _downloadProgress = 0;
        _hasDownloadError = false;
        _errorMessage = string.Empty;
        NotifyStateChanged();
    }

    public void ReportProgress(WhisperModelDownloadProgress progress)
    {
        _downloadedBytes = progress.BytesDownloaded;
        _expectedBytes = progress.ExpectedBytes;
        _downloadProgress = progress.Percentage;
        NotifyStateChanged();
    }

    public void SetDownloadError(string message)
    {
        _isDownloading = false;
        _downloadProgress = 0;
        _hasDownloadError = true;
        _errorMessage = message;
        NotifyStateChanged();
    }

    public void SetManagerBusy(bool value)
    {
        if (_isManagerBusy == value)
        {
            return;
        }

        _isManagerBusy = value;
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
    }

    public void SetTranscriptionActive(bool value)
    {
        if (_isTranscriptionActive == value)
        {
            return;
        }

        _isTranscriptionActive = value;
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(HasDownloadError));
        OnPropertyChanged(nameof(DownloadProgress));
        OnPropertyChanged(nameof(DownloadActionText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanCancelDownload));
    }

    private static string FormatBytes(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        return bytes >= megabyte
            ? $"{bytes / megabyte:0.#} MB"
            : $"{bytes / 1024d:0.#} KB";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
