using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Transcription.Whisper;

public sealed record WhisperModelState(
    WhisperModelSize ModelSize,
    string ModelPath,
    bool IsInstalled,
    long InstalledBytes,
    long ExpectedBytes);
