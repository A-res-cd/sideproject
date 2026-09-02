using System.Text.Json;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Data.History;

public sealed class TranscriptHistoryStore
{
    public const int MaximumSessions = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _historyFilePath;

    public TranscriptHistoryStore(string? historyFilePath = null)
    {
        _historyFilePath = historyFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsTranscriber",
            "history",
            "sessions.json");
    }

    public string HistoryFilePath => _historyFilePath;

    public async Task<TranscriptHistoryLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task UpsertAsync(
        TranscriptHistorySession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsValid(session))
        {
            throw new ArgumentException("Transcript history session is invalid.", nameof(session));
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loadResult = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var sessions = loadResult.Sessions.ToList();
            var existing = sessions.FindIndex(item => item.SessionId == session.SessionId);
            if (existing >= 0 && sessions[existing].LastSavedAt > session.LastSavedAt)
            {
                return;
            }

            if (existing >= 0)
            {
                session = MergeSessions(sessions[existing], session);
                sessions.RemoveAt(existing);
            }

            sessions.Add(session);
            var newestSessions = sessions
                .OrderByDescending(item => item.LastSavedAt)
                .Take(MaximumSessions)
                .ToArray();
            await SaveUnlockedAsync(newestSessions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task DeleteAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loadResult = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var remainingSessions = loadResult.Sessions
                .Where(session => session.SessionId != sessionId)
                .ToArray();
            await SaveUnlockedAsync(remainingSessions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<TranscriptHistoryLoadResult> LoadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        var temporaryFilePath = _historyFilePath + ".tmp";
        var sourceFilePath = File.Exists(_historyFilePath)
            ? _historyFilePath
            : File.Exists(temporaryFilePath)
                ? temporaryFilePath
                : null;
        if (sourceFilePath is null)
        {
            return new TranscriptHistoryLoadResult([], false, null);
        }

        try
        {
            HistoryDocument? document;
            await using (var stream = new FileStream(
                sourceFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16_384,
                useAsync: true))
            {
                document = await JsonSerializer.DeserializeAsync<HistoryDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            if (document is null ||
                document.SchemaVersion != HistoryDocument.CurrentSchemaVersion ||
                document.Sessions is null ||
                document.Sessions.Any(session => !IsValid(session)))
            {
                return QuarantineCorruptedFile(sourceFilePath);
            }

            if (string.Equals(
                sourceFilePath,
                temporaryFilePath,
                StringComparison.OrdinalIgnoreCase))
            {
                File.Move(temporaryFilePath, _historyFilePath, overwrite: true);
            }

            return new TranscriptHistoryLoadResult(
                document.Sessions
                    .OrderByDescending(session => session.LastSavedAt)
                    .Take(MaximumSessions)
                    .ToArray(),
                false,
                null);
        }
        catch (JsonException)
        {
            return QuarantineCorruptedFile(sourceFilePath);
        }
        catch (NotSupportedException)
        {
            return QuarantineCorruptedFile(sourceFilePath);
        }
    }

    private async Task SaveUnlockedAsync(
        IReadOnlyList<TranscriptHistorySession> sessions,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_historyFilePath)
            ?? throw new InvalidOperationException("History path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryFilePath = _historyFilePath + ".tmp";
        await using (var stream = new FileStream(
            temporaryFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16_384,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new HistoryDocument(HistoryDocument.CurrentSchemaVersion, sessions),
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryFilePath, _historyFilePath, overwrite: true);
    }

    private static bool IsValid(TranscriptHistorySession session) =>
        session.SchemaVersion == TranscriptHistorySession.CurrentSchemaVersion &&
        session.SessionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.ApplicationName) &&
        !string.IsNullOrWhiteSpace(session.ProcessName) &&
        session.Segments is { Count: > 0 };

    private static TranscriptHistorySession MergeSessions(
        TranscriptHistorySession existing,
        TranscriptHistorySession updated)
    {
        var segments = existing.Segments
            .Concat(updated.Segments)
            .Distinct()
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.End)
            .ToArray();

        return updated with
        {
            StartedAt = existing.StartedAt < updated.StartedAt
                ? existing.StartedAt
                : updated.StartedAt,
            ActiveDuration = existing.ActiveDuration > updated.ActiveDuration
                ? existing.ActiveDuration
                : updated.ActiveDuration,
            Segments = segments,
        };
    }

    private static TranscriptHistoryLoadResult QuarantineCorruptedFile(
        string sourceFilePath)
    {
        var quarantinedFilePath = sourceFilePath +
            $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        File.Move(sourceFilePath, quarantinedFilePath, overwrite: true);
        return new TranscriptHistoryLoadResult([], true, quarantinedFilePath);
    }

    private sealed record HistoryDocument(
        int SchemaVersion,
        IReadOnlyList<TranscriptHistorySession> Sessions)
    {
        public const int CurrentSchemaVersion = 1;
    }
}
