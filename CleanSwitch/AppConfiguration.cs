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

        var options = settings.CleanSwitch;

        if (options.RestartDelaySeconds < 0)
        {
            throw new InvalidOperationException("CleanSwitch:RestartDelaySeconds must be zero or greater.");
        }

        if (!string.IsNullOrWhiteSpace(options.RecoveryGuid) &&
            !Guid.TryParse(options.RecoveryGuid.Trim(), out _))
        {
            throw new InvalidOperationException(
                $"CleanSwitch:RecoveryGuid '{options.RecoveryGuid}' is not a BCD GUID. " +
                "Expected a value like {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}. " +
                "Find it with 'bcdedit /enum all /v' from an elevated prompt, or leave it empty to let " +
                "CleanSwitch read the running entry's recoverysequence.");
        }

        if (string.IsNullOrWhiteSpace(options.StateFileName))
        {
            options.StateFileName = CleanSwitchOptions.DefaultStateFileName;
        }

        return options;
    }

    private sealed class AppSettingsFile
    {
        public CleanSwitchOptions CleanSwitch { get; set; } = new();
    }
}
