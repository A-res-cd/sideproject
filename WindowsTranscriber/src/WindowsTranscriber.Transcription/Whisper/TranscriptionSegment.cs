namespace WindowsTranscriber.Transcription.Whisper;

public sealed record TranscriptionSegment(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    float Confidence = 1,
    float NoSpeechProbability = 0,
    string? LanguageCode = null);
