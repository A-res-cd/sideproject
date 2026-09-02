using System.IO;
using Microsoft.Win32;
using WindowsTranscriber.Core.Models;
using WindowsTranscriber.Export;

namespace WindowsTranscriber.App.Services;

public sealed class TranscriptExportDialogService
{
    private readonly TranscriptExporter _exporter = new();

    public async Task<string?> ExportAsync(
        IReadOnlyCollection<TranscriptSegment> segments,
        string applicationName,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("There is no transcript to export.");
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Download transcript",
            FileName = CreateDefaultFileName(applicationName),
            AddExtension = true,
            OverwritePrompt = true,
            Filter =
                "Text transcript (*.txt)|*.txt|" +
                "SubRip subtitles (*.srt)|*.srt|" +
                "WebVTT subtitles (*.vtt)|*.vtt",
            FilterIndex = 1,
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var format = GetFormat(dialog.FileName, dialog.FilterIndex);
        await _exporter.WriteAsync(
            dialog.FileName,
            segments,
            format,
            cancellationToken);

        return dialog.FileName;
    }

    private static TranscriptExportFormat GetFormat(string fileName, int filterIndex) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".srt" => TranscriptExportFormat.Srt,
            ".vtt" => TranscriptExportFormat.Vtt,
            ".txt" => TranscriptExportFormat.Txt,
            _ => filterIndex switch
            {
                2 => TranscriptExportFormat.Srt,
                3 => TranscriptExportFormat.Vtt,
                _ => TranscriptExportFormat.Txt,
            },
        };

    private static string CreateDefaultFileName(string applicationName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeApplicationName = new string(applicationName
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();

        if (safeApplicationName.Length == 0)
        {
            safeApplicationName = "Transcript";
        }
        else if (safeApplicationName.Length > 60)
        {
            safeApplicationName = safeApplicationName[..60].TrimEnd();
        }

        return $"{safeApplicationName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
    }
}
