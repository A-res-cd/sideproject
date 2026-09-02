namespace WindowsTranscriber.Core.Models;

public sealed record TranscriptRecoverySession(
    int SchemaVersion,
    Guid SessionId,
    int ProcessId,
    string ProcessName,
    string ApplicationName,
    DateTimeOffset StartedAt,
    TimeSpan ActiveDuration,
    DateTimeOffset LastSavedAt,
    IReadOnlyList<TranscriptSegment> Segments)
{
    public const int CurrentSchemaVersion = 1;
}
