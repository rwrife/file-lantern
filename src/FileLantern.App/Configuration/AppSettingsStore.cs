using System.Text.Json;
using FileLantern.Core.LocalAi;

namespace FileLantern.App.Configuration;

public sealed class AppSettings
{
    public LocalAiQuerySettings LocalAi { get; set; } = new();
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static AppSettings Load(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        var defaults = new AppSettings();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        if (!File.Exists(settingsPath))
        {
            Save(settingsPath, defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? defaults;
            return Normalize(loaded);
        }
        catch
        {
            return defaults;
        }
    }

    private static void Save(string settingsPath, AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(settingsPath, json);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.LocalAi ??= new LocalAiQuerySettings();

        if (string.IsNullOrWhiteSpace(settings.LocalAi.EndpointUrl))
        {
            settings.LocalAi.EndpointUrl = "http://localhost:11434";
        }

        if (string.IsNullOrWhiteSpace(settings.LocalAi.Model))
        {
            settings.LocalAi.Model = "qwen2.5:1.5b-instruct";
        }

        settings.LocalAi.TimeoutSeconds = Math.Clamp(settings.LocalAi.TimeoutSeconds, 1, 30);
        return settings;
    }
}
