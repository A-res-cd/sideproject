using System.Runtime.InteropServices;

namespace WindowsTranscriber.Audio.Processing;

internal sealed class Pcm16StereoResampler
{
    private readonly List<float> _sourceSamples = [];
    private readonly double _sourceFramesPerOutputFrame;
    private double _nextSourcePosition;

    internal Pcm16StereoResampler(int sourceSampleRate, int outputSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRate);

        _sourceFramesPerOutputFrame = (double)sourceSampleRate / outputSampleRate;
    }

    internal float[] Convert(ReadOnlySpan<short> interleavedStereoSamples)
    {
        if (interleavedStereoSamples.Length < 2)
        {
            return [];
        }

        var sourceFrameCount = interleavedStereoSamples.Length / 2;
        var originalSourceCount = _sourceSamples.Count;
        _sourceSamples.EnsureCapacity(originalSourceCount + sourceFrameCount);
        CollectionsMarshal.SetCount(
            _sourceSamples,
            originalSourceCount + sourceFrameCount);
        var destination = CollectionsMarshal.AsSpan(_sourceSamples)
            .Slice(originalSourceCount, sourceFrameCount);

        for (var frameIndex = 0; frameIndex < sourceFrameCount; frameIndex++)
        {
            var sourceIndex = frameIndex * 2;
            var left = interleavedStereoSamples[sourceIndex] / 32768f;
            var right = interleavedStereoSamples[sourceIndex + 1] / 32768f;
            destination[frameIndex] = (left + right) * 0.5f;
        }

        var availableSourceFrames =
            _sourceSamples.Count - 1 - _nextSourcePosition;
        if (availableSourceFrames <= 0)
        {
            return [];
        }

        var output = new float[(int)Math.Ceiling(
            availableSourceFrames / _sourceFramesPerOutputFrame)];
        var outputIndex = 0;

        while (_nextSourcePosition + 1 < _sourceSamples.Count)
        {
            var firstIndex = (int)_nextSourcePosition;
            var fraction = (float)(_nextSourcePosition - firstIndex);
            var first = _sourceSamples[firstIndex];
            var second = _sourceSamples[firstIndex + 1];

            output[outputIndex++] = first + ((second - first) * fraction);
            _nextSourcePosition += _sourceFramesPerOutputFrame;
        }

        if (_sourceSamples.Count > 1)
        {
            var removableCount = Math.Min((int)_nextSourcePosition, _sourceSamples.Count - 1);
            if (removableCount > 0)
            {
                _sourceSamples.RemoveRange(0, removableCount);
                _nextSourcePosition -= removableCount;
            }
        }

        if (outputIndex != output.Length)
        {
            Array.Resize(ref output, outputIndex);
        }

        return output;
    }
}
