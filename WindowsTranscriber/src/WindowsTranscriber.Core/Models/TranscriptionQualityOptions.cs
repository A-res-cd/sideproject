namespace WindowsTranscriber.Core.Models;

public sealed record TranscriptionQualityOptions(
    TranscriptionQualityPreset Preset,
    float MinimumConfidence,
    float MaximumNoSpeechProbability,
    int OverlapMilliseconds,
    bool MarkUncertainSegments)
{
    public static TranscriptionQualityOptions Default { get; } = new(
        TranscriptionQualityPreset.Balanced,
        0.35f,
        0.65f,
        750,
        true);

    public TranscriptionQualityOptions Normalize() => this with
    {
        Preset = Enum.IsDefined(Preset) ? Preset : Default.Preset,
        MinimumConfidence = Math.Clamp(MinimumConfidence, 0.05f, 0.95f),
        MaximumNoSpeechProbability = Math.Clamp(
            MaximumNoSpeechProbability,
            0.05f,
            0.95f),
        OverlapMilliseconds = Math.Clamp(OverlapMilliseconds, 0, 2_000),
    };
}
