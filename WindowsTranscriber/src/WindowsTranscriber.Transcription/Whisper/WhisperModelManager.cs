using Whisper.net.Ggml;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Transcription.Whisper;

public sealed class WhisperModelManager
{
    private static readonly SemaphoreSlim ModelFileLock = new(1, 1);

    public WhisperModelState GetState(WhisperModelSize modelSize)
    {
        var modelPath = WhisperTranscriptionService.GetModelPath(modelSize);
        var installedBytes = File.Exists(modelPath)
            ? new FileInfo(modelPath).Length
            : 0;

        return new WhisperModelState(
            modelSize,
            modelPath,
            installedBytes > 0,
            installedBytes,
            GetExpectedBytes(modelSize));
    }

    public async Task DownloadAsync(
        WhisperModelSize modelSize,
        IProgress<WhisperModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ModelFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = GetState(modelSize);
            if (state.IsInstalled)
            {
                progress?.Report(new WhisperModelDownloadProgress(
                    modelSize,
                    state.InstalledBytes,
                    state.ExpectedBytes,
                    100));
                return;
            }

            var modelDirectory = Path.GetDirectoryName(state.ModelPath)
                ?? throw new InvalidOperationException("Model path has no directory.");
            Directory.CreateDirectory(modelDirectory);
            var temporaryPath = state.ModelPath + ".download";

            try
            {
                using var modelStream = await WhisperGgmlDownloader.Default
                    .GetGgmlModelAsync(GetGgmlType(modelSize))
                    .ConfigureAwait(false);
                long downloadedBytes = 0;
                await using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81_920,
                    useAsync: true))
                {
                    var buffer = new byte[81_920];
                    while (true)
                    {
                        var bytesRead = await modelStream.ReadAsync(
                            buffer,
                            cancellationToken).ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        await destination.WriteAsync(
                            buffer.AsMemory(0, bytesRead),
                            cancellationToken).ConfigureAwait(false);
                        downloadedBytes += bytesRead;
                        var percentage = Math.Min(
                            99,
                            downloadedBytes * 100d / state.ExpectedBytes);
                        progress?.Report(new WhisperModelDownloadProgress(
                            modelSize,
                            downloadedBytes,
                            state.ExpectedBytes,
                            percentage));
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, state.ModelPath, overwrite: true);

                progress?.Report(new WhisperModelDownloadProgress(
                    modelSize,
                    downloadedBytes,
                    state.ExpectedBytes,
                    100));
            }
            catch
            {
                File.Delete(temporaryPath);
                throw;
            }
        }
        finally
        {
            ModelFileLock.Release();
        }
    }

    public async Task DeleteAsync(
        WhisperModelSize modelSize,
        CancellationToken cancellationToken = default)
    {
        await ModelFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modelPath = WhisperTranscriptionService.GetModelPath(modelSize);
            File.Delete(modelPath);
            File.Delete(modelPath + ".download");
        }
        finally
        {
            ModelFileLock.Release();
        }
    }

    private static long GetExpectedBytes(WhisperModelSize modelSize) => modelSize switch
    {
        WhisperModelSize.Tiny => 77_691_713,
        WhisperModelSize.Base => 147_951_465,
        WhisperModelSize.Small => 487_601_967,
        WhisperModelSize.TinyEnglish => 77_691_713,
        WhisperModelSize.BaseEnglish => 147_951_465,
        WhisperModelSize.SmallEnglish => 487_601_967,
        _ => throw new ArgumentOutOfRangeException(nameof(modelSize), modelSize, null),
    };

    private static GgmlType GetGgmlType(WhisperModelSize modelSize) => modelSize switch
    {
        WhisperModelSize.Tiny => GgmlType.Tiny,
        WhisperModelSize.Base => GgmlType.Base,
        WhisperModelSize.Small => GgmlType.Small,
        WhisperModelSize.TinyEnglish => GgmlType.TinyEn,
        WhisperModelSize.BaseEnglish => GgmlType.BaseEn,
        WhisperModelSize.SmallEnglish => GgmlType.SmallEn,
        _ => throw new ArgumentOutOfRangeException(nameof(modelSize), modelSize, null),
    };
}
