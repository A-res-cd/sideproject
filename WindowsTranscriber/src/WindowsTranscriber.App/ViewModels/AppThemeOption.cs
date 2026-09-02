using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.App.ViewModels;

public sealed record AppThemeOption(
    AppThemeMode Mode,
    string DisplayName);
