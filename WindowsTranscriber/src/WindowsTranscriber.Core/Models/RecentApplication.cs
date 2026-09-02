namespace WindowsTranscriber.Core.Models;

public sealed record RecentApplication(
    string ProcessName,
    string DisplayName,
    DateTimeOffset LastUsedAt);
