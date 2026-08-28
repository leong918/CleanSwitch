using System.Text.Json;
using CleanSwitch.Models;

namespace CleanSwitch;

internal static class AppConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static CleanSwitchOptions Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Could not find configuration file: {path}");
        }

        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<AppSettingsFile>(json, JsonOptions)
            ?? throw new InvalidOperationException("appsettings.json could not be parsed.");

        if (settings.CleanSwitch.RestartDelaySeconds < 0)
        {
            throw new InvalidOperationException("CleanSwitch:RestartDelaySeconds must be zero or greater.");
        }

        return settings.CleanSwitch;
    }

    private sealed class AppSettingsFile
    {
        public CleanSwitchOptions CleanSwitch { get; set; } = new();
    }
}
