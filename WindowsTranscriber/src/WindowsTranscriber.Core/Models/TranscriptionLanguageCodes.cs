namespace WindowsTranscriber.Core.Models;

public static class TranscriptionLanguageCodes
{
    public const string English = "en";
    public const string Filipino = "tl";
    public const string FilipinoEnglish = "tl-en";

    public static bool IsEnglish(string? languageCode) =>
        string.Equals(languageCode, English, StringComparison.OrdinalIgnoreCase);

    public static bool IsFilipino(string? languageCode) =>
        string.Equals(languageCode, Filipino, StringComparison.OrdinalIgnoreCase);

    public static bool IsFilipinoEnglish(string? languageCode) =>
        string.Equals(
            languageCode,
            FilipinoEnglish,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsSupported(string? languageCode) =>
        IsEnglish(languageCode) ||
        IsFilipino(languageCode) ||
        IsFilipinoEnglish(languageCode);
}
