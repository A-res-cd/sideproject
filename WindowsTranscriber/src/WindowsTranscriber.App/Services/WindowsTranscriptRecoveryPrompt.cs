using System.Windows;
using WindowsTranscriber.Core.Models;
using MessageBox = System.Windows.MessageBox;

namespace WindowsTranscriber.App.Services;

public sealed class WindowsTranscriptRecoveryPrompt : ITranscriptRecoveryPrompt
{
    public bool ShouldRestore(TranscriptRecoverySession session) =>
        MessageBox.Show(
            $"An autosaved transcript was found.{Environment.NewLine}{Environment.NewLine}" +
            $"Application: {session.ApplicationName}{Environment.NewLine}" +
            $"Segments: {session.Segments.Count}{Environment.NewLine}" +
            $"Last saved: {session.LastSavedAt.ToLocalTime():g}{Environment.NewLine}{Environment.NewLine}" +
            "Restore it? Choosing No permanently discards this recovery.",
            "Restore autosaved transcript?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes) == MessageBoxResult.Yes;
}
