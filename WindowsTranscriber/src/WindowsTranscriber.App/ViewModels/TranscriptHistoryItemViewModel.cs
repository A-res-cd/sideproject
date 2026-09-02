using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.App.ViewModels;

public sealed class TranscriptHistoryItemViewModel
{
    public TranscriptHistoryItemViewModel(TranscriptHistorySession session)
    {
        Session = session;
    }

    public TranscriptHistorySession Session { get; }

    public Guid SessionId => Session.SessionId;

    public string ApplicationName => Session.ApplicationName;

    public string StartedAtText => Session.StartedAt.ToLocalTime().ToString("g");

    public string DetailsText =>
        $"{FormatDuration(Session.ActiveDuration)} · " +
        $"{Session.Segments.Count} segment{(Session.Segments.Count == 1 ? string.Empty : "s")}";

    public string PreviewText
    {
        get
        {
            var preview = string.Join(" ", Session.Segments.Take(2).Select(item => item.Text));
            return preview.Length <= 120 ? preview : preview[..117] + "...";
        }
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes:00}:{duration.Seconds:00}";
}
