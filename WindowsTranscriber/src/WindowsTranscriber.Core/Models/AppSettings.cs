using System.Text.Json.Serialization;

namespace WindowsTranscriber.Core.Models;

[method: JsonConstructor]
public sealed record AppSettings(
    int SchemaVersion,
    double TranscriptFontSize,
    bool AutoScrollEnabled,
    string LanguageCode,
    WhisperModelSize ModelSize,
    TranscriptionQualityPreset QualityPreset,
    double MinimumConfidence,
    double MaximumNoSpeechProbability,
    int OverlapMilliseconds,
    bool MarkUncertainSegments,
    bool GlobalHotkeyEnabled,
    bool MinimizeToTray,
    bool MinimizeWhileTranscribing,
    bool NotificationsEnabled,
    AppThemeMode ThemeMode,
    IReadOnlyList<RecentApplication> RecentApplications,
    WindowPlacementSettings? WindowPlacement)
{
    public const int CurrentSchemaVersion = 2;
    public const double MinimumFontSize = 12;
    public const double MaximumFontSize = 32;

    public static AppSettings Default { get; } = new(
        CurrentSchemaVersion,
        18,
        true,
        TranscriptionLanguageCodes.FilipinoEnglish,
        WhisperModelSize.Small,
        TranscriptionQualityPreset.Balanced,
        0.35,
        0.65,
        750,
        true,
        true,
        false,
        false,
        true,
        AppThemeMode.System,
        [],
        null);

    public AppSettings(
        int schemaVersion,
        double transcriptFontSize,
        bool autoScrollEnabled,
        string languageCode,
        WhisperModelSize modelSize)
        : this(
            schemaVersion,
            transcriptFontSize,
            autoScrollEnabled,
            languageCode,
            modelSize,
            Default.QualityPreset,
            Default.MinimumConfidence,
            Default.MaximumNoSpeechProbability,
            Default.OverlapMilliseconds,
            Default.MarkUncertainSegments,
            Default.GlobalHotkeyEnabled,
            Default.MinimizeToTray,
            Default.MinimizeWhileTranscribing,
            Default.NotificationsEnabled,
            Default.ThemeMode,
            Default.RecentApplications,
            Default.WindowPlacement)
    {
    }

    public AppSettings Normalize(IReadOnlySet<string> supportedLanguageCodes)
    {
        var normalizedLanguageCode = supportedLanguageCodes.Contains(LanguageCode)
            ? LanguageCode
            : Default.LanguageCode;
        var normalizedModelSize = Enum.IsDefined(ModelSize)
            ? ModelSize
            : Default.ModelSize;
        var usesLegacyDefaults = SchemaVersion < CurrentSchemaVersion;
        var normalizedRecentApplications = (RecentApplications ?? [])
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ProcessName) &&
                !string.IsNullOrWhiteSpace(item.DisplayName))
            .GroupBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.LastUsedAt).First())
            .OrderByDescending(item => item.LastUsedAt)
            .Take(5)
            .ToArray();

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            TranscriptFontSize = Math.Clamp(
                TranscriptFontSize,
                MinimumFontSize,
                MaximumFontSize),
            LanguageCode = normalizedLanguageCode,
            ModelSize = normalizedModelSize,
            QualityPreset = usesLegacyDefaults || !Enum.IsDefined(QualityPreset)
                ? Default.QualityPreset
                : QualityPreset,
            MinimumConfidence = usesLegacyDefaults
                ? Default.MinimumConfidence
                : Math.Clamp(MinimumConfidence, 0.05, 0.95),
            MaximumNoSpeechProbability = usesLegacyDefaults
                ? Default.MaximumNoSpeechProbability
                : Math.Clamp(MaximumNoSpeechProbability, 0.05, 0.95),
            OverlapMilliseconds = usesLegacyDefaults
                ? Default.OverlapMilliseconds
                : Math.Clamp(OverlapMilliseconds, 0, 2_000),
            MarkUncertainSegments = usesLegacyDefaults
                ? Default.MarkUncertainSegments
                : MarkUncertainSegments,
            GlobalHotkeyEnabled = usesLegacyDefaults
                ? Default.GlobalHotkeyEnabled
                : GlobalHotkeyEnabled,
            MinimizeToTray = usesLegacyDefaults
                ? Default.MinimizeToTray
                : MinimizeToTray,
            MinimizeWhileTranscribing = usesLegacyDefaults
                ? Default.MinimizeWhileTranscribing
                : MinimizeWhileTranscribing,
            NotificationsEnabled = usesLegacyDefaults
                ? Default.NotificationsEnabled
                : NotificationsEnabled,
            ThemeMode = usesLegacyDefaults || !Enum.IsDefined(ThemeMode)
                ? Default.ThemeMode
                : ThemeMode,
            RecentApplications = usesLegacyDefaults
                ? Default.RecentApplications
                : normalizedRecentApplications,
            WindowPlacement = NormalizeWindowPlacement(WindowPlacement),
        };
    }

    private static WindowPlacementSettings? NormalizeWindowPlacement(
        WindowPlacementSettings? placement)
    {
        if (placement is null ||
            !double.IsFinite(placement.Left) ||
            !double.IsFinite(placement.Top) ||
            !double.IsFinite(placement.Width) ||
            !double.IsFinite(placement.Height) ||
            placement.Width < 660 ||
            placement.Height < 460)
        {
            return null;
        }

        return placement with
        {
            Width = Math.Min(placement.Width, 8_000),
            Height = Math.Min(placement.Height, 8_000),
        };
    }
}
