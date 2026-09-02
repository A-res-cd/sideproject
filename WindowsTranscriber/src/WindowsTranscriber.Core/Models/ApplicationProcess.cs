namespace WindowsTranscriber.Core.Models;

public sealed record ApplicationProcess(
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string WindowTitle);