using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Data.Settings;

public sealed record AppSettingsLoadResult(
    AppSettings Settings,
    bool WasCorrupted,
    string? QuarantinedFilePath);
