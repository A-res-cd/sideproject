using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Data.Recovery;

public sealed record TranscriptRecoveryLoadResult(
    TranscriptRecoverySession? Session,
    bool WasCorrupted,
    string? QuarantinedFilePath);
