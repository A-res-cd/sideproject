using System.Windows;

namespace WindowsTranscriber.App.Services;

public sealed class WindowsClipboardService : IClipboardService
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
