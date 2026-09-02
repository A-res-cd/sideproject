using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Transcription.Whisper;

public sealed record WhisperModelDownloadProgress(
    WhisperModelSize ModelSize,
    long BytesDownloaded,
    long ExpectedBytes,
    double Percentage);
