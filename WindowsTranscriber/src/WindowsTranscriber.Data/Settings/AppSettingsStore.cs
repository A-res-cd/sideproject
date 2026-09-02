using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Data.Settings;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _settingsFilePath;

    public AppSettingsStore(string? settingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsTranscriber",
            "settings.json");
    }

    public string SettingsFilePath => _settingsFilePath;

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath)
                ?? throw new InvalidOperationException("Settings path has no directory.");
            Directory.CreateDirectory(directory);

            var temporaryFilePath = _settingsFilePath + ".tmp";
            await using (var stream = new FileStream(
                temporaryFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8_192,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryFilePath, _settingsFilePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<AppSettingsLoadResult> LoadAsync(
        IReadOnlySet<string> supportedLanguageCodes,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettingsLoadResult(AppSettings.Default, false, null);
            }

            try
            {
                AppSettings? settings;
                await using (var stream = new FileStream(
                    _settingsFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 8_192,
                    useAsync: true))
                {
                    settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                        stream,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                }

                if (settings is null ||
                    settings.SchemaVersion < 1 ||
                    settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
                {
                    return QuarantineCorruptedFile();
                }

                return new AppSettingsLoadResult(
                    settings.Normalize(supportedLanguageCodes),
                    false,
                    null);
            }
            catch (JsonException)
            {
                return QuarantineCorruptedFile();
            }
            catch (NotSupportedException)
            {
                return QuarantineCorruptedFile();
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private AppSettingsLoadResult QuarantineCorruptedFile()
    {
        var quarantinedFilePath = _settingsFilePath +
            $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        File.Move(_settingsFilePath, quarantinedFilePath, overwrite: true);
        return new AppSettingsLoadResult(
            AppSettings.Default,
            true,
            quarantinedFilePath);
    }
}
