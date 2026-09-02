using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using WindowsTranscriber.Audio.Interop;
using WindowsTranscriber.Audio.Processing;

namespace WindowsTranscriber.Audio.Capture;

[SupportedOSPlatform("windows10.0")]
public sealed class ProcessLoopbackCapture : IAsyncDisposable
{
    private const int CaptureSampleRate = 44_100;
    private const int OutputSampleRate = 16_000;
    private const int CaptureChannelCount = 2;

    private readonly Channel<float[]> _sampleChannel = Channel.CreateBounded<float[]>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _captureCancellation;
    private Thread? _captureThread;
    private bool _started;

    public ChannelReader<float[]> Samples => _sampleChannel.Reader;

    public async Task StartAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            throw new PlatformNotSupportedException("Process audio capture requires Windows 10 or newer.");
        }

        TaskCompletionSource startedCompletion;

        lock (_lifecycleLock)
        {
            if (_started)
            {
                throw new InvalidOperationException("Audio capture is already running.");
            }

            _started = true;
            _captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _captureThread = new Thread(
                () => CaptureThreadMain((uint)processId, _captureCancellation.Token, startedCompletion))
            {
                IsBackground = true,
                Name = "WindowsTranscriber WASAPI Capture",
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }

        await startedCompletion.Task.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        Thread? captureThread;

        lock (_lifecycleLock)
        {
            _captureCancellation?.Cancel();
            captureThread = _captureThread;
        }

        if (captureThread is not null && captureThread.IsAlive)
        {
            await Task.Run(() => captureThread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        lock (_lifecycleLock)
        {
            _captureCancellation?.Dispose();
            _captureCancellation = null;
            _captureThread = null;
            _started = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private void CaptureThreadMain(
        uint processId,
        CancellationToken cancellationToken,
        TaskCompletionSource startedCompletion)
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        var audioClientStarted = false;

        try
        {
            audioClient = ActivateProcessAudioClientAsync(processId).GetAwaiter().GetResult();

            using var audioReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

            var format = WaveFormatEx.CreatePcm16(CaptureSampleRate, CaptureChannelCount);
            var formatPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());

            try
            {
                Marshal.StructureToPtr(format, formatPointer, false);

                ThrowIfFailed(audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                    0,
                    0,
                    formatPointer,
                    IntPtr.Zero));
            }
            finally
            {
                Marshal.FreeHGlobal(formatPointer);
            }

            ThrowIfFailed(audioClient.SetEventHandle(
                audioReadyEvent.SafeWaitHandle.DangerousGetHandle()));

            ThrowIfFailed(audioClient.GetService(
                WasapiInterop.AudioCaptureClientInterfaceId,
                out var captureClientObject));

            captureClient = (IAudioCaptureClient)captureClientObject;
            ThrowIfFailed(audioClient.Start());
            audioClientStarted = true;
            startedCompletion.TrySetResult();

            var resampler = new Pcm16StereoResampler(CaptureSampleRate, OutputSampleRate);
            var waitHandles = new WaitHandle[] { audioReadyEvent, cancellationToken.WaitHandle };

            while (!cancellationToken.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waitHandles) == 1)
                {
                    break;
                }

                DrainAudioPackets(captureClient, resampler);
            }
        }
        catch (Exception exception)
        {
            var captureException = exception is COMException or Win32Exception
                ? new InvalidOperationException(
                    "Windows could not start process-specific audio capture. " +
                    "Confirm the selected application is still running and Windows supports process loopback.",
                    exception)
                : exception;

            startedCompletion.TrySetException(captureException);
            _sampleChannel.Writer.TryComplete(captureException);
        }
        finally
        {
            if (audioClientStarted && audioClient is not null)
            {
                _ = audioClient.Stop();
            }

            ReleaseComObject(captureClient);
            ReleaseComObject(audioClient);
            _sampleChannel.Writer.TryComplete();
        }
    }

    private void DrainAudioPackets(
        IAudioCaptureClient captureClient,
        Pcm16StereoResampler resampler)
    {
        ThrowIfFailed(captureClient.GetNextPacketSize(out var nextPacketFrames));

        while (nextPacketFrames > 0)
        {
            ThrowIfFailed(captureClient.GetBuffer(
                out var data,
                out var frameCount,
                out var flags,
                out _,
                out _));

            short[]? capturedSamples = null;
            try
            {
                var sampleCount = checked((int)frameCount * CaptureChannelCount);
                capturedSamples = ArrayPool<short>.Shared.Rent(sampleCount);

                if (flags.HasFlag(AudioClientBufferFlags.Silent) || data == IntPtr.Zero)
                {
                    capturedSamples.AsSpan(0, sampleCount).Clear();
                }
                else
                {
                    Marshal.Copy(data, capturedSamples, 0, sampleCount);
                }

                var convertedSamples = resampler.Convert(
                    capturedSamples.AsSpan(0, sampleCount));
                if (convertedSamples.Length > 0)
                {
                    _sampleChannel.Writer.TryWrite(convertedSamples);
                }
            }
            finally
            {
                if (capturedSamples is not null)
                {
                    ArrayPool<short>.Shared.Return(capturedSamples);
                }

                ThrowIfFailed(captureClient.ReleaseBuffer(frameCount));
            }

            ThrowIfFailed(captureClient.GetNextPacketSize(out nextPacketFrames));
        }
    }

    private static async Task<IAudioClient> ActivateProcessAudioClientAsync(uint processId)
    {
        var activationParameters = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = processId,
                ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree,
            },
        };

        var activationParametersPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<AudioClientActivationParams>());
        var propVariantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        IntPtr completionHandlerPointer = IntPtr.Zero;

        try
        {
            Marshal.StructureToPtr(activationParameters, activationParametersPointer, false);

            var propVariant = new PropVariantBlob
            {
                VariantType = WasapiInterop.VariantBlob,
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                BlobData = activationParametersPointer,
            };
            Marshal.StructureToPtr(propVariant, propVariantPointer, false);

            var completionHandler = new AudioInterfaceActivationHandler();
            completionHandlerPointer = Marshal.GetComInterfaceForObject<
                AudioInterfaceActivationHandler,
                IActivateAudioInterfaceCompletionHandler>(completionHandler);

            ThrowIfFailed(WasapiInterop.ActivateAudioInterfaceAsync(
                WasapiInterop.ProcessLoopbackDevice,
                WasapiInterop.AudioClientInterfaceId,
                propVariantPointer,
                completionHandlerPointer,
                out var activationOperation));

            if (activationOperation != IntPtr.Zero)
            {
                Marshal.Release(activationOperation);
            }

            return await completionHandler.Completion.ConfigureAwait(false);
        }
        finally
        {
            if (completionHandlerPointer != IntPtr.Zero)
            {
                Marshal.Release(completionHandlerPointer);
            }

            Marshal.FreeHGlobal(propVariantPointer);
            Marshal.FreeHGlobal(activationParametersPointer);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            _ = Marshal.FinalReleaseComObject(instance);
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class AudioInterfaceActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<IAudioClient> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<IAudioClient> Completion => _completion.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                ThrowIfFailed(activateOperation.GetActivateResult(
                    out var activationResult,
                    out var activatedInterface));
                ThrowIfFailed(activationResult);

                if (activatedInterface is not IAudioClient audioClient)
                {
                    throw new InvalidOperationException(
                        "Windows activated process loopback without returning an audio client.");
                }

                _completion.TrySetResult(audioClient);
                return WasapiInterop.S_OK;
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
                return Marshal.GetHRForException(exception);
            }
        }
    }
}
