using System.Runtime.CompilerServices;
using System.Text;
using Whisper.net;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Transcription.Whisper;

public sealed class WhisperTranscriptionService : IAsyncDisposable
{
    private const string FilipinoEnglishPrompt =
        "Magandang araw! Kumusta ka? Ayos lang ako. Today pag-uusapan natin " +
        "ang mga kailangan gawin. Okay na ito, pero kailangan pa nating tingnan " +
        "at ayusin. Please sabihin mo ulit kung hindi malinaw. Pakisend na lang " +
        "ang file, tapos mag-follow up tayo bukas. Maraming salamat!";

    private static readonly IReadOnlySet<string> GermanMarkers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aber", "auch", "das", "dem", "den", "der", "die", "ein",
            "eine", "frei", "ganz", "gar", "ich", "im", "ist",
            "kann", "mit", "nicht", "und", "von", "wie", "zu",
        };

    private static readonly IReadOnlySet<string> IndonesianMarkers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "banget", "belum", "bisa", "dari", "dengan", "dong", "gak",
            "gini", "jadi", "kalau", "kamu", "karena", "kita", "lo", "lu",
            "nampak", "nge", "nggak", "nih", "saya", "sih", "sudah",
            "tapi", "tidak", "tinggal", "untuk", "usir", "yang",
        };

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly WhisperModelManager _modelManager = new();
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private WhisperModelSize? _configuredModelSize;
    private string? _configuredLanguageCode;
    private TranscriptionQualityOptions? _configuredQualityOptions;

    public WhisperModelSize? ConfiguredModelSize => _configuredModelSize;

    public string? ConfiguredLanguageCode => _configuredLanguageCode;

    public TranscriptionQualityOptions? ConfiguredQualityOptions =>
        _configuredQualityOptions;

    public string ModelPath => GetModelPath(
        _configuredModelSize ?? WhisperModelSize.Base);

    public async Task InitializeAsync(
        Action<string>? reportStatus = null,
        CancellationToken cancellationToken = default) =>
        await InitializeAsync(
            WhisperModelSize.Small,
            TranscriptionLanguageCodes.FilipinoEnglish,
            TranscriptionQualityOptions.Default,
            reportStatus,
            cancellationToken).ConfigureAwait(false);

    public async Task InitializeAsync(
        WhisperModelSize modelSize,
        string languageCode,
        Action<string>? reportStatus = null,
        CancellationToken cancellationToken = default) =>
        await InitializeAsync(
            modelSize,
            languageCode,
            TranscriptionQualityOptions.Default,
            reportStatus,
            cancellationToken).ConfigureAwait(false);

    public async Task InitializeAsync(
        WhisperModelSize modelSize,
        string languageCode,
        TranscriptionQualityOptions qualityOptions,
        Action<string>? reportStatus = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        ArgumentNullException.ThrowIfNull(qualityOptions);
        if (!TranscriptionLanguageCodes.IsSupported(languageCode))
        {
            throw new ArgumentException(
                "Only English, Filipino, and Filipino + English are supported.",
                nameof(languageCode));
        }

        qualityOptions = qualityOptions.Normalize();

        if (_processor is not null &&
            _configuredModelSize == modelSize &&
            string.Equals(
                _configuredLanguageCode,
                languageCode,
                StringComparison.OrdinalIgnoreCase) &&
            _configuredQualityOptions == qualityOptions)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_processor is not null &&
                _configuredModelSize == modelSize &&
                string.Equals(
                    _configuredLanguageCode,
                    languageCode,
                    StringComparison.OrdinalIgnoreCase) &&
                _configuredQualityOptions == qualityOptions)
            {
                return;
            }

            await DisposeLoadedModelAsync().ConfigureAwait(false);

            var modelPath = GetModelPath(modelSize);
            var modelState = _modelManager.GetState(modelSize);
            if (!modelState.IsInstalled)
            {
                reportStatus?.Invoke(
                    $"Downloading Whisper {GetModelDisplayName(modelSize)} model " +
                    $"({GetModelDownloadSize(modelSize)})...");
                await _modelManager.DownloadAsync(
                    modelSize,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            reportStatus?.Invoke($"Loading Whisper {GetModelDisplayName(modelSize)} model...");

            var initialized = await Task.Run(
                () =>
                {
                    WhisperFactory? factory = null;
                    try
                    {
                        factory = WhisperFactory.FromPath(modelPath);
                        var builder = factory.CreateBuilder()
                            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
                            .WithProbabilities()
                            .WithNoSpeechThreshold(qualityOptions.MaximumNoSpeechProbability);

                        if (TranscriptionLanguageCodes.IsFilipinoEnglish(languageCode))
                        {
                            // Whisper selects one language token for a decode. Tagalog
                            // keeps Filipino speech accurate while the mixed prompt
                            // preserves the English words common in natural Taglish.
                            builder = builder
                                .WithLanguage(TranscriptionLanguageCodes.Filipino)
                                .WithPrompt(FilipinoEnglishPrompt)
                                .WithNoContext();
                        }
                        else
                        {
                            builder = builder.WithLanguage(
                                TranscriptionLanguageCodes.IsEnglish(languageCode)
                                    ? TranscriptionLanguageCodes.English
                                    : TranscriptionLanguageCodes.Filipino);
                        }

                        builder = qualityOptions.Preset switch
                        {
                            TranscriptionQualityPreset.LowLatency => builder
                                .WithTemperature(0)
                                .WithGreedySamplingStrategy(strategy =>
                                    strategy.WithBestOf(1)),
                            TranscriptionQualityPreset.HighAccuracy => builder
                                .WithTemperature(0)
                                .WithBeamSearchSamplingStrategy(strategy =>
                                    strategy.WithBeamSize(5)),
                            _ when TranscriptionLanguageCodes.IsFilipinoEnglish(
                                languageCode) => builder
                                .WithTemperature(0)
                                .WithBeamSearchSamplingStrategy(strategy =>
                                    strategy.WithBeamSize(5)),
                            _ => builder
                                .WithTemperature(0)
                                .WithGreedySamplingStrategy(strategy =>
                                    strategy.WithBestOf(2)),
                        };

                        var processor = builder.Build();

                        return (Factory: factory, Processor: processor);
                    }
                    catch
                    {
                        factory?.Dispose();
                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);

            _factory = initialized.Factory;
            _processor = initialized.Processor;
            _configuredModelSize = modelSize;
            _configuredLanguageCode = languageCode;
            _configuredQualityOptions = qualityOptions;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async IAsyncEnumerable<TranscriptionSegment> TranscribeAsync(
        float[] samples,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_processor is null)
        {
            throw new InvalidOperationException("The Whisper model has not been initialized.");
        }

        if (samples.Length < 201)
        {
            yield break;
        }

        await foreach (var segment in _processor
            .ProcessAsync(samples, cancellationToken)
            .ConfigureAwait(false))
        {
            var text = segment.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && IsTargetLanguageText(text))
            {
                var confidence = float.IsFinite(segment.Probability)
                    ? Math.Clamp(segment.Probability, 0, 1)
                    : 0;
                var noSpeechProbability = float.IsFinite(segment.NoSpeechProbability)
                    ? Math.Clamp(segment.NoSpeechProbability, 0, 1)
                    : 1;
                yield return new TranscriptionSegment(
                    text,
                    segment.Start,
                    segment.End,
                    confidence,
                    noSpeechProbability,
                    TranscriptionLanguageCodes.IsFilipinoEnglish(
                        _configuredLanguageCode)
                        ? TranscriptionLanguageCodes.FilipinoEnglish
                        : segment.Language);
            }
        }
    }

    private static bool IsTargetLanguageText(string text)
    {
        if (ContainsUnsupportedWritingSystem(text))
        {
            return false;
        }

        var words = GetWords(text);
        return !LooksLikeUnsupportedLanguage(words, GermanMarkers) &&
            !LooksLikeUnsupportedLanguage(words, IndonesianMarkers);
    }

    private static bool ContainsUnsupportedWritingSystem(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            var value = rune.Value;
            var isLatin = value is >= 0x0041 and <= 0x007A or
                >= 0x00C0 and <= 0x024F or
                >= 0x1E00 and <= 0x1EFF;
            if (!isLatin)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetWords(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                word.Append(rune.ToString());
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private static bool LooksLikeUnsupportedLanguage(
        IReadOnlyList<string> words,
        IReadOnlySet<string> markers)
    {
        if (words.Count < 4)
        {
            return false;
        }

        var markerCount = words.Count(markers.Contains);
        return markerCount >= 2 && markerCount * 5 >= words.Count * 2;
    }

    public async ValueTask DisposeAsync()
    {
        await _initializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeLoadedModelAsync().ConfigureAwait(false);
        }
        finally
        {
            _initializationLock.Release();
            _initializationLock.Dispose();
        }
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeLoadedModelAsync().ConfigureAwait(false);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public static string GetModelPath(WhisperModelSize modelSize) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsTranscriber",
        "models",
        modelSize switch
        {
            WhisperModelSize.Tiny => "ggml-tiny.bin",
            WhisperModelSize.Base => "ggml-base.bin",
            WhisperModelSize.Small => "ggml-small.bin",
            WhisperModelSize.TinyEnglish => "ggml-tiny.en.bin",
            WhisperModelSize.BaseEnglish => "ggml-base.en.bin",
            WhisperModelSize.SmallEnglish => "ggml-small.en.bin",
            _ => throw new ArgumentOutOfRangeException(nameof(modelSize), modelSize, null),
        });

    private async Task DisposeLoadedModelAsync()
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }

        _factory?.Dispose();
        _factory = null;
        _configuredModelSize = null;
        _configuredLanguageCode = null;
        _configuredQualityOptions = null;
    }

    private static string GetModelDisplayName(WhisperModelSize modelSize) => modelSize switch
    {
        WhisperModelSize.Tiny => "Tiny",
        WhisperModelSize.Base => "Base",
        WhisperModelSize.Small => "Small",
        WhisperModelSize.TinyEnglish => "Tiny English-only",
        WhisperModelSize.BaseEnglish => "Base English-only",
        WhisperModelSize.SmallEnglish => "Small English-only",
        _ => throw new ArgumentOutOfRangeException(nameof(modelSize), modelSize, null),
    };

    private static string GetModelDownloadSize(WhisperModelSize modelSize) => modelSize switch
    {
        WhisperModelSize.Tiny => "about 75 MB",
        WhisperModelSize.Base => "about 142 MB",
        WhisperModelSize.Small => "about 466 MB",
        WhisperModelSize.TinyEnglish => "about 75 MB",
        WhisperModelSize.BaseEnglish => "about 142 MB",
        WhisperModelSize.SmallEnglish => "about 466 MB",
        _ => throw new ArgumentOutOfRangeException(nameof(modelSize), modelSize, null),
    };
}
