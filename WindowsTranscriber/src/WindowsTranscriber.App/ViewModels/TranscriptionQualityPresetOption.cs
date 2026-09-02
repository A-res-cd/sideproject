using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.App.ViewModels;

public sealed record TranscriptionQualityPresetOption(
    TranscriptionQualityPreset Preset,
    string DisplayName,
    string Description);
