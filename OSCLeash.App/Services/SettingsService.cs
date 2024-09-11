using FluentResults;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace OSCLeash.App.Services;

internal interface ISettingsService
{
    Settings Value { get; }
}

internal class SettingsService : IHostedService, ISettingsService
{
    private FileSystemWatcher? _settingsWatcher;

    private Settings _settings;

    public Settings Value { get { return _settings; } set { _settings = value; OnSettingsUpdated?.Invoke(this, EventArgs.Empty); } }

    public EventHandler? OnSettingsUpdated;

    public readonly string ConfigFilePath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oscleash", "config.json");
    private readonly ILogger<SettingsService> logger;

    public SettingsService(ILogger<SettingsService> logger)
    {
        this.logger = logger;
        var manifestEmbeddedProvider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly);

        var configDirectory = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oscleash");
        if (!Directory.Exists(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }

        if (!File.Exists(ConfigFilePath))
        {
            logger.LogInformation("No configuration file exist in {configDirectory}. Creating {configFile}", configDirectory, ConfigFilePath);

            var configTemplate = manifestEmbeddedProvider.GetFileInfo("Resources/config.json");
            using var configStream = configTemplate.CreateReadStream();

            using var configFile = File.Create(ConfigFilePath);
            configStream.CopyTo(configFile);
        }

        var settingsResult = Result.Try(() => JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigFilePath)));
        if (settingsResult is { IsFailed: true } || settingsResult.Value == null)
        {
            if (settingsResult.IsFailed)
            {
                var exception = settingsResult.Reasons.OfType<ExceptionalError>().FirstOrDefault()?.Exception;
                logger.LogError(exception, "Failed to deserialize settings file");
            }

            var configTemplate = manifestEmbeddedProvider.GetFileInfo("Resources/config.json");
            using var configStream = configTemplate.CreateReadStream();
            _settings = JsonSerializer.Deserialize<Settings>(configStream)!;
        }
        else
        {
            _settings = settingsResult.Value;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settingsWatcher = new FileSystemWatcher(Path.GetDirectoryName(ConfigFilePath)!)
        {
            NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite,
            Filter = "config.json"
        };
        _settingsWatcher.Created += SettingsWatcher_Changed;
        _settingsWatcher.Changed += SettingsWatcher_Changed;
        _settingsWatcher.Deleted += SettingsWatcher_Changed;
        _settingsWatcher.Renamed += SettingsWatcher_Changed;

        _settingsWatcher.EnableRaisingEvents = true;

        return Task.CompletedTask;
    }

    public void Save()
    {
        File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(_settings));
    }

    private void SettingsWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        if (File.Exists(ConfigFilePath))
        {
            try
            {
                using var configFile = File.OpenRead(ConfigFilePath);
                var settings = JsonSerializer.Deserialize<Settings>(configFile);
                if (settings == null)
                {
                    logger.LogWarning("Got null value back for JSON config.");
                    return;
                }

                OnSettingsUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load updated configuration file. Keeping existing settings...");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settingsWatcher?.Dispose();

        if (File.Exists(ConfigFilePath))
        {
            File.WriteAllText(
                ConfigFilePath,
                JsonSerializer.Serialize(_settings)
            );
        }

        return Task.CompletedTask;
    }
}
