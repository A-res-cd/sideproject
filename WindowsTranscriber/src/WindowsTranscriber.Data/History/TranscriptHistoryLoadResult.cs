using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Data.History;

public sealed record TranscriptHistoryLoadResult(
    IReadOnlyList<TranscriptHistorySession> Sessions,
    bool WasCorrupted,
    string? QuarantinedFilePath);
