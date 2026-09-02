namespace WindowsTranscriber.Audio.Processing;

public sealed class AudioChunker
{
    public const int SampleRate = 16_000;
    public static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OverlapDuration = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan StepDuration = WindowDuration - OverlapDuration;

    private const int WindowSampleCount = SampleRate * 5;
    private readonly float[] _sampleBuffer = new float[WindowSampleCount * 2];
    private readonly int _stepSampleCount;
    private int _bufferStart;
    private int _bufferCount;

    public AudioChunker(TimeSpan? overlapDuration = null)
    {
        ConfiguredOverlapDuration = overlapDuration ?? OverlapDuration;
        if (ConfiguredOverlapDuration < TimeSpan.Zero ||
            ConfiguredOverlapDuration > TimeSpan.FromSeconds(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlapDuration),
                "Overlap must be between 0 and 2 seconds.");
        }

        var overlapSampleCount = (int)Math.Round(
            ConfiguredOverlapDuration.TotalSeconds * SampleRate);
        _stepSampleCount = WindowSampleCount - overlapSampleCount;
        ConfiguredStepDuration = TimeSpan.FromSeconds(
            (double)_stepSampleCount / SampleRate);
    }

    public TimeSpan ConfiguredOverlapDuration { get; }

    public TimeSpan ConfiguredStepDuration { get; }

    public IReadOnlyList<float[]> Add(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return [];
        }

        List<float[]>? windows = null;
        while (!samples.IsEmpty)
        {
            var writeIndex = (_bufferStart + _bufferCount) % _sampleBuffer.Length;
            var freeCount = _sampleBuffer.Length - _bufferCount;
            var copyCount = Math.Min(
                samples.Length,
                Math.Min(freeCount, _sampleBuffer.Length - writeIndex));
            samples[..copyCount].CopyTo(_sampleBuffer.AsSpan(writeIndex, copyCount));
            _bufferCount += copyCount;
            samples = samples[copyCount..];

            while (_bufferCount >= WindowSampleCount)
            {
                var window = new float[WindowSampleCount];
                var firstCopyCount = Math.Min(
                    WindowSampleCount,
                    _sampleBuffer.Length - _bufferStart);
                _sampleBuffer.AsSpan(_bufferStart, firstCopyCount)
                    .CopyTo(window);
                if (firstCopyCount < WindowSampleCount)
                {
                    _sampleBuffer.AsSpan(0, WindowSampleCount - firstCopyCount)
                        .CopyTo(window.AsSpan(firstCopyCount));
                }

                (windows ??= []).Add(window);
                _bufferStart = (_bufferStart + _stepSampleCount) %
                    _sampleBuffer.Length;
                _bufferCount -= _stepSampleCount;
            }
        }

        return windows ?? [];
    }

    public void Reset()
    {
        _bufferStart = 0;
        _bufferCount = 0;
    }
}
