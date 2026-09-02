using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.App.ViewModels;

public sealed record WhisperModelOption(
    WhisperModelSize ModelSize,
    string DisplayName,
    string Description);
