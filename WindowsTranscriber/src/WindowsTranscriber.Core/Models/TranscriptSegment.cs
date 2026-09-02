namespace WindowsTranscriber.Core.Models;

public sealed record TranscriptSegment(
    Guid SessionId,
    int ProcessId,
    string ApplicationName,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? LanguageCode = null,
    double Confidence = 1,
    bool IsUncertain = false);
