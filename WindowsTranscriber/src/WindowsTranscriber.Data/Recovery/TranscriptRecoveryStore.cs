using System.Text.Json;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Data.Recovery;

public sealed class TranscriptRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _recoveryFilePath;

    public TranscriptRecoveryStore(string? recoveryFilePath = null)
    {
        _recoveryFilePath = recoveryFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsTranscriber",
            "recovery",
            "current-session.json");
    }

    public string RecoveryFilePath => _recoveryFilePath;

    public async Task SaveAsync(
        TranscriptRecoverySession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_recoveryFilePath)
                ?? throw new InvalidOperationException("Recovery path has no directory.");
            Directory.CreateDirectory(directory);

            var temporaryFilePath = _recoveryFilePath + ".tmp";
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
                    session,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryFilePath, _recoveryFilePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<TranscriptRecoveryLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporaryFilePath = _recoveryFilePath + ".tmp";
            var sourceFilePath = File.Exists(_recoveryFilePath)
                ? _recoveryFilePath
                : File.Exists(temporaryFilePath)
                    ? temporaryFilePath
                    : null;
            if (sourceFilePath is null)
            {
                return new TranscriptRecoveryLoadResult(null, false, null);
            }

            try
            {
                TranscriptRecoverySession? session;
                await using (var stream = new FileStream(
                    sourceFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16_384,
                    useAsync: true))
                {
                    session = await JsonSerializer
                        .DeserializeAsync<TranscriptRecoverySession>(
                            stream,
                            JsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (session is null ||
                    session.SchemaVersion != TranscriptRecoverySession.CurrentSchemaVersion ||
                    session.Segments is null ||
                    string.IsNullOrWhiteSpace(session.ApplicationName) ||
                    string.IsNullOrWhiteSpace(session.ProcessName))
                {
                    return QuarantineCorruptedFile(sourceFilePath);
                }

                if (string.Equals(
                    sourceFilePath,
                    temporaryFilePath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(temporaryFilePath, _recoveryFilePath, overwrite: true);
                }

                return new TranscriptRecoveryLoadResult(session, false, null);
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
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(_recoveryFilePath);
            File.Delete(_recoveryFilePath + ".tmp");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static TranscriptRecoveryLoadResult QuarantineCorruptedFile(
        string sourceFilePath)
    {
        var quarantinedFilePath = sourceFilePath +
            $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        File.Move(sourceFilePath, quarantinedFilePath, overwrite: true);
        return new TranscriptRecoveryLoadResult(null, true, quarantinedFilePath);
    }
}
