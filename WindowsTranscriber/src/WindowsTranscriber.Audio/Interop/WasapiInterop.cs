using System.Runtime.InteropServices;

namespace WindowsTranscriber.Audio.Interop;

internal static class WasapiInterop
{
    internal const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    internal const ushort WaveFormatPcm = 1;
    internal const ushort VariantBlob = 65;
    internal const int S_OK = 0;

    internal static readonly Guid AudioClientInterfaceId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static readonly Guid AudioCaptureClientInterfaceId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid interfaceId,
        IntPtr activationParameters,
        IntPtr completionHandler,
        out IntPtr activationOperation);
}

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,
    ExcludeTargetProcessTree = 1,
}

internal enum AudioClientShareMode
{
    Shared = 0,
    Exclusive = 1,
}

[Flags]
internal enum AudioClientStreamFlags : uint
{
    Loopback = 0x00020000,
    EventCallback = 0x00040000,
}

[Flags]
internal enum AudioClientBufferFlags : uint
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    internal uint TargetProcessId;
    internal ProcessLoopbackMode ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    internal AudioClientActivationType ActivationType;
    internal AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    internal ushort VariantType;
    internal ushort Reserved1;
    internal ushort Reserved2;
    internal ushort Reserved3;
    internal uint BlobSize;
    internal IntPtr BlobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    internal ushort FormatTag;
    internal ushort Channels;
    internal uint SamplesPerSecond;
    internal uint AverageBytesPerSecond;
    internal ushort BlockAlign;
    internal ushort BitsPerSample;
    internal ushort ExtraSize;

    internal static WaveFormatEx CreatePcm16(uint samplesPerSecond, ushort channels)
    {
        const ushort bitsPerSample = 16;
        var blockAlign = checked((ushort)(channels * (bitsPerSample / 8)));

        return new WaveFormatEx
        {
            FormatTag = WasapiInterop.WaveFormatPcm,
            Channels = channels,
            SamplesPerSecond = samplesPerSecond,
            AverageBytesPerSecond = samplesPerSecond * blockAlign,
            BlockAlign = blockAlign,
            BitsPerSample = bitsPerSample,
            ExtraSize = 0,
        };
    }
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(
        out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

[ComVisible(true)]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        AudioClientShareMode shareMode,
        AudioClientStreamFlags streamFlags,
        long bufferDuration,
        long periodicity,
        IntPtr waveFormat,
        IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrameCount);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint currentPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(
        AudioClientShareMode shareMode,
        IntPtr waveFormat,
        out IntPtr closestMatchWaveFormat);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(
        in Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out IntPtr data,
        out uint frameCount,
        out AudioClientBufferFlags flags,
        out ulong devicePosition,
        out ulong performanceCounterPosition);

    [PreserveSig]
    int ReleaseBuffer(uint frameCount);

    [PreserveSig]
    int GetNextPacketSize(out uint nextPacketFrameCount);
}
