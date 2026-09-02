namespace WindowsTranscriber.Core.Models;

public sealed record WindowPlacementSettings(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized);
