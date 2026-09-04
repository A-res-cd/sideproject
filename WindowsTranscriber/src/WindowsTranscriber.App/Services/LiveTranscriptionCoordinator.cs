using System.Threading.Channels;
using WindowsTranscriber.Audio.Capture;
using WindowsTranscriber.Audio.Processing;
using WindowsTranscriber.Core.Models;
using WindowsTranscriber.Transcription.Whisper;

namespace WindowsTranscriber.App.Services;

public sealed class LiveTranscriptionCoordinator : IAsyncDisposable
{
    private const int FilipinoEnglishMinimumOverlapMilliseconds = 1_250;
    private const float FilipinoEnglishMinimumConfidence = 0.45f;
    private const float FilipinoEnglishMaximumNoSpeechProbability = 0.55f;

    private readonly object _stateLock = new();
    private readonly WhisperTranscriptionService _transcriptionService = new();

    private CancellationTokenSource? _runCancellation;
    private ProcessLoopbackCapture? _capture;
    private Task? _pipelineTask;
    private TaskCompletionSource? _startCompletion;
    private bool _isRunning;
    private bool _isPaused;
    private int _pauseGeneration;

    public event Action<string>? StatusChanged;
    public event Action<TranscriptSegment>? TranscriptReceived;
    public event Action<Exception>? Failed;
    public event Action? Stopped;

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _isRunning;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
            {
                return _isPaused;
            }
        }
    }

    public Task StartAsync(
        int processId,
        string applicationName,
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        StartAsync(
            processId,
            applicationName,
            sessionId,
            WhisperModelSize.Small,
            TranscriptionLanguageCodes.FilipinoEnglish,
            TranscriptionQualityOptions.Default,
            cancellationToken);

    public async Task StartAsync(
        int processId,
        string applicationName,
        Guid sessionId,
        WhisperModelSize modelSize,
        string languageCode,
        CancellationToken cancellationToken = default) =>
        await StartAsync(
            processId,
            applicationName,
            sessionId,
            modelSize,
            languageCode,
            TranscriptionQualityOptions.Default,
            cancellationToken).ConfigureAwait(false);

    public async Task StartAsync(
        int processId,
        string applicationName,
        Guid sessionId,
        WhisperModelSize modelSize,
        string languageCode,
        TranscriptionQualityOptions qualityOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(qualityOptions);
        qualityOptions = qualityOptions.Normalize();
        if (TranscriptionLanguageCodes.IsFilipinoEnglish(languageCode))
        {
            // Extra shared audio keeps a language switch at a window boundary
            // from losing the first short word in either language.
            qualityOptions = qualityOptions with
            {
                MinimumConfidence = Math.Max(
                    qualityOptions.MinimumConfidence,
                    FilipinoEnglishMinimumConfidence),
                MaximumNoSpeechProbability = Math.Min(
                    qualityOptions.MaximumNoSpeechProbability,
                    FilipinoEnglishMaximumNoSpeechProbability),
                OverlapMilliseconds = Math.Max(
                    qualityOptions.OverlapMilliseconds,
                    FilipinoEnglishMinimumOverlapMilliseconds),
            };
        }

        CancellationTokenSource runCancellation;
        TaskCompletionSource startCompletion;

        lock (_stateLock)
        {
            if (_runCancellation is not null)
            {
                throw new InvalidOperationException("Live transcription is already starting or running.");
            }

            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _runCancellation = runCancellation;
            _startCompletion = startCompletion;
            _isPaused = false;
            _pauseGeneration++;
        }

        ProcessLoopbackCapture? capture = null;

        try
        {
            ReportStatus("Preparing local transcription...");
            await _transcriptionService
                .InitializeAsync(
                    modelSize,
                    languageCode,
                    qualityOptions,
                    ReportStatus,
                    runCancellation.Token)
                .ConfigureAwait(false);

            runCancellation.Token.ThrowIfCancellationRequested();
            ReportStatus("Starting process audio capture...");

            capture = new ProcessLoopbackCapture();
            await capture.StartAsync(processId, runCancellation.Token).ConfigureAwait(false);

            var windowChannel = Channel.CreateBounded<TimestampedAudioWindow>(
                new BoundedChannelOptions(2)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });

            var chunker = new AudioChunker(
                TimeSpan.FromMilliseconds(qualityOptions.OverlapMilliseconds));
            var deduplicator = new TranscriptDeduplicator();
            var capturePumpTask = PumpCapturedSamplesAsync(
                capture,
                chunker,
                windowChannel.Writer,
                runCancellation.Token);
            var transcriptionTask = TranscribeWindowsAsync(
                windowChannel.Reader,
                deduplicator,
                processId,
                applicationName,
                sessionId,
                qualityOptions,
                chunker.ConfiguredStepDuration,
                chunker.ConfiguredOverlapDuration,
                runCancellation.Token);

            lock (_stateLock)
            {
                if (!ReferenceEquals(_runCancellation, runCancellation))
                {
                    throw new OperationCanceledException(runCancellation.Token);
                }

                _capture = capture;
                _isRunning = true;
                _pipelineTask = ObservePipelineAsync(
                    Task.WhenAll(capturePumpTask, transcriptionTask),
                    capture,
                    runCancellation);
            }

            ReportStatus("Listening for audio from the selected application...");
        }
        catch
        {
            runCancellation.Cancel();

            if (capture is not null)
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }

            var ownsState = false;
            lock (_stateLock)
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                {
                    _runCancellation = null;
                    _capture = null;
                    _pipelineTask = null;
                    _isRunning = false;
                    _isPaused = false;
                    _pauseGeneration++;
                    ownsState = true;
                }
            }

            if (ownsState)
            {
                runCancellation.Dispose();
            }

            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_startCompletion, startCompletion))
                {
                    _startCompletion = null;
                }
            }

            startCompletion.TrySetResult();
        }
    }

    public bool Pause()
    {
        lock (_stateLock)
        {
            if (!_isRunning || _isPaused)
            {
                return false;
            }

            _isPaused = true;
            _pauseGeneration++;
        }

        ReportStatus("Transcription paused.");
        return true;
    }

    public bool Resume()
    {
        lock (_stateLock)
        {
            if (!_isRunning || !_isPaused)
            {
                return false;
            }

            _isPaused = false;
            _pauseGeneration++;
        }

        ReportStatus("Listening for audio from the selected application...");
        return true;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? runCancellation;
        ProcessLoopbackCapture? capture;
        Task? pipelineTask;
        Task? startTask;

        lock (_stateLock)
        {
            runCancellation = _runCancellation;
            capture = _capture;
            pipelineTask = _pipelineTask;
            startTask = _startCompletion?.Task;

            _runCancellation = null;
            _capture = null;
            _pipelineTask = null;
            _isRunning = false;
            _isPaused = false;
            _pauseGeneration++;
        }

        if (runCancellation is null)
        {
            return;
        }

        runCancellation.Cancel();

        if (startTask is not null)
        {
            await startTask.ConfigureAwait(false);
        }

        if (capture is null)
        {
            runCancellation.Dispose();
            ReportStatus("Transcription stopped.");
            return;
        }

        await capture.StopAsync().ConfigureAwait(false);

        if (pipelineTask is not null)
        {
            await pipelineTask.ConfigureAwait(false);
        }

        runCancellation.Dispose();
        ReportStatus("Transcription stopped.");
    }

    public async Task UnloadModelAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_runCancellation is not null)
            {
                throw new InvalidOperationException(
                    "Stop transcription before deleting a model.");
            }
        }

        await _transcriptionService.UnloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _transcriptionService.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpCapturedSamplesAsync(
        ProcessLoopbackCapture capture,
        AudioChunker chunker,
        ChannelWriter<TimestampedAudioWindow> windows,
        CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        var nextWindowStart = TimeSpan.Zero;
        long activeSampleCount = 0;
        var observedPauseGeneration = GetPauseState().Generation;

        try
        {
            await foreach (var samples in capture.Samples
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                var pauseState = GetPauseState();
                if (pauseState.Generation != observedPauseGeneration)
                {
                    chunker.Reset();
                    nextWindowStart = TimeSpan.FromSeconds(
                        (double)activeSampleCount / AudioChunker.SampleRate);
                    observedPauseGeneration = pauseState.Generation;
                }

                if (pauseState.IsPaused)
                {
                    continue;
                }

                activeSampleCount += samples.Length;
                foreach (var window in chunker.Add(samples))
                {
                    windows.TryWrite(new TimestampedAudioWindow(
                        window,
                        nextWindowStart,
                        observedPauseGeneration));
                    nextWindowStart += chunker.ConfiguredStepDuration;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            completionError = exception;
            throw;
        }
        finally
        {
            windows.TryComplete(completionError);
        }
    }

    private async Task TranscribeWindowsAsync(
        ChannelReader<TimestampedAudioWindow> windows,
        TranscriptDeduplicator deduplicator,
        int processId,
        string applicationName,
        Guid sessionId,
        TranscriptionQualityOptions qualityOptions,
        TimeSpan stepDuration,
        TimeSpan overlapDuration,
        CancellationToken cancellationToken)
    {
        TimeSpan? previousWindowStart = null;
        int? previousPauseGeneration = null;

        await foreach (var audioWindow in windows
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!IsWindowCurrent(audioWindow.PauseGeneration))
            {
                previousWindowStart = null;
                previousPauseGeneration = null;
                continue;
            }

            var followsPreviousWindow = previousWindowStart.HasValue &&
                previousPauseGeneration == audioWindow.PauseGeneration &&
                audioWindow.Start - previousWindowStart.Value == stepDuration;

            if (IsEffectivelySilent(audioWindow.Samples))
            {
                ReportStatusIfWindowCurrent(
                    audioWindow.PauseGeneration,
                    "Listening... no speech detected yet.");
                previousWindowStart = audioWindow.Start;
                previousPauseGeneration = audioWindow.PauseGeneration;
                continue;
            }

            ReportStatusIfWindowCurrent(
                audioWindow.PauseGeneration,
                "Transcribing captured audio locally...");

            await foreach (var segment in _transcriptionService
                .TranscribeAsync(audioWindow.Samples, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!IsWindowCurrent(audioWindow.PauseGeneration))
                {
                    break;
                }

                if (segment.NoSpeechProbability >
                        qualityOptions.MaximumNoSpeechProbability ||
                    segment.Confidence < qualityOptions.MinimumConfidence)
                {
                    continue;
                }

                if (followsPreviousWindow && segment.End <= overlapDuration)
                {
                    continue;
                }

                var novelText = deduplicator.GetNovelText(segment.Text);
                if (novelText.Length > 0)
                {
                    var transcriptSegment = new TranscriptSegment(
                        sessionId,
                        processId,
                        applicationName,
                        audioWindow.Start + segment.Start,
                        audioWindow.Start + segment.End,
                        novelText,
                        segment.LanguageCode,
                        segment.Confidence,
                        qualityOptions.MarkUncertainSegments &&
                            IsUncertain(segment, qualityOptions));

                    if (!TryReportTranscript(
                        audioWindow.PauseGeneration,
                        transcriptSegment))
                    {
                        break;
                    }
                }
            }

            if (IsWindowCurrent(audioWindow.PauseGeneration))
            {
                previousWindowStart = audioWindow.Start;
                previousPauseGeneration = audioWindow.PauseGeneration;
                ReportStatusIfWindowCurrent(
                    audioWindow.PauseGeneration,
                    "Listening for more audio...");
            }
            else
            {
                previousWindowStart = null;
                previousPauseGeneration = null;
            }
        }
    }

    private async Task ObservePipelineAsync(
        Task pipelineTask,
        ProcessLoopbackCapture capture,
        CancellationTokenSource runCancellation)
    {
        try
        {
            await pipelineTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception.GetBaseException());
        }
        finally
        {
            runCancellation.Cancel();
            await capture.StopAsync().ConfigureAwait(false);

            var ownsState = false;
            lock (_stateLock)
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                {
                    _runCancellation = null;
                    _capture = null;
                    _pipelineTask = null;
                    _isRunning = false;
                    _isPaused = false;
                    _pauseGeneration++;
                    ownsState = true;
                }
            }

            if (ownsState)
            {
                runCancellation.Dispose();
                Stopped?.Invoke();
            }
        }
    }

    private static bool IsEffectivelySilent(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0)
        {
            return true;
        }

        double sumOfSquares = 0;
        for (var index = 0; index < samples.Count; index++)
        {
            sumOfSquares += samples[index] * samples[index];
        }

        var rootMeanSquare = Math.Sqrt(sumOfSquares / samples.Count);
        return rootMeanSquare < 0.001;
    }

    private static bool IsUncertain(
        TranscriptionSegment segment,
        TranscriptionQualityOptions qualityOptions) =>
        segment.Confidence < Math.Min(
            0.95f,
            qualityOptions.MinimumConfidence + 0.15f) ||
        segment.NoSpeechProbability > Math.Max(
            0.05f,
            qualityOptions.MaximumNoSpeechProbability - 0.15f);

    private void ReportStatus(string status) => StatusChanged?.Invoke(status);

    private PauseState GetPauseState()
    {
        lock (_stateLock)
        {
            return new PauseState(_isPaused, _pauseGeneration);
        }
    }

    private bool IsWindowCurrent(int pauseGeneration)
    {
        var pauseState = GetPauseState();
        return !pauseState.IsPaused && pauseState.Generation == pauseGeneration;
    }

    private void ReportStatusIfWindowCurrent(int pauseGeneration, string status)
    {
        lock (_stateLock)
        {
            if (!_isPaused && _pauseGeneration == pauseGeneration)
            {
                StatusChanged?.Invoke(status);
            }
        }
    }

    private bool TryReportTranscript(
        int pauseGeneration,
        TranscriptSegment transcriptSegment)
    {
        lock (_stateLock)
        {
            if (_isPaused || _pauseGeneration != pauseGeneration)
            {
                return false;
            }

            TranscriptReceived?.Invoke(transcriptSegment);
            return true;
        }
    }

    private readonly record struct PauseState(bool IsPaused, int Generation);

    private sealed record TimestampedAudioWindow(
        float[] Samples,
        TimeSpan Start,
        int PauseGeneration);
}
