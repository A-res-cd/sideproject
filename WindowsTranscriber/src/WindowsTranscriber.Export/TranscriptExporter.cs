using System.Text;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Export;

public sealed class TranscriptExporter
{
    private static readonly UTF8Encoding Utf8WithoutByteOrderMark = new(false);

    public string CreateContent(
        IEnumerable<TranscriptSegment> segments,
        TranscriptExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var orderedSegments = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.End)
            .ToArray();

        if (orderedSegments.Length == 0)
        {
            throw new InvalidOperationException("There is no transcript to export.");
        }

        return format switch
        {
            TranscriptExportFormat.Txt => CreateTextContent(orderedSegments),
            TranscriptExportFormat.Srt => CreateSrtContent(orderedSegments),
            TranscriptExportFormat.Vtt => CreateVttContent(orderedSegments),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    public async Task WriteAsync(
        string filePath,
        IEnumerable<TranscriptSegment> segments,
        TranscriptExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var content = CreateContent(segments, format);
        await File.WriteAllTextAsync(
            filePath,
            content,
            Utf8WithoutByteOrderMark,
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateTextContent(IReadOnlyList<TranscriptSegment> segments)
    {
        var lines = segments.Select(segment =>
            $"[{FormatReadableTimestamp(segment.Start)}] {GetDisplayText(segment)}");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string CreateSrtContent(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var (start, end) = GetValidCueRange(segment);

            builder.AppendLine((index + 1).ToString());
            builder.Append(FormatSubtitleTimestamp(start, ','));
            builder.Append(" --> ");
            builder.AppendLine(FormatSubtitleTimestamp(end, ','));
            builder.AppendLine(GetDisplayText(segment));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string CreateVttContent(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WEBVTT");
        builder.AppendLine();

        foreach (var segment in segments)
        {
            var (start, end) = GetValidCueRange(segment);

            builder.Append(FormatSubtitleTimestamp(start, '.'));
            builder.Append(" --> ");
            builder.AppendLine(FormatSubtitleTimestamp(end, '.'));
            builder.AppendLine(GetDisplayText(segment));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static (TimeSpan Start, TimeSpan End) GetValidCueRange(
        TranscriptSegment segment)
    {
        var start = segment.Start < TimeSpan.Zero ? TimeSpan.Zero : segment.Start;
        var end = segment.End <= start
            ? start + TimeSpan.FromMilliseconds(1)
            : segment.End;

        return (start, end);
    }

    private static string FormatReadableTimestamp(TimeSpan timestamp)
    {
        timestamp = timestamp < TimeSpan.Zero ? TimeSpan.Zero : timestamp;
        if (timestamp.TotalHours >= 1)
        {
            return $"{(long)timestamp.TotalHours:00}:{timestamp.Minutes:00}:{timestamp.Seconds:00}";
        }

        return $"{timestamp.Minutes:00}:{timestamp.Seconds:00}";
    }

    private static string FormatSubtitleTimestamp(TimeSpan timestamp, char separator)
    {
        timestamp = timestamp < TimeSpan.Zero ? TimeSpan.Zero : timestamp;
        return $"{(long)timestamp.TotalHours:00}:{timestamp.Minutes:00}:{timestamp.Seconds:00}" +
            $"{separator}{timestamp.Milliseconds:000}";
    }

    private static string NormalizeText(string text) =>
        text.Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    private static string GetDisplayText(TranscriptSegment segment) =>
        (segment.IsUncertain ? "[uncertain] " : string.Empty) +
        NormalizeText(segment.Text);
}
