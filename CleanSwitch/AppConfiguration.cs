using System.Text.Json;
using CleanSwitch.Models;
using CleanSwitch.Recovery;

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

        if (!string.IsNullOrWhiteSpace(options.RecoveryDataVolumeGptId))
        {
            if (!VolumeLocator.TryParseGptId(options.RecoveryDataVolumeGptId, out var gptPartitionId))
            {
                throw new InvalidOperationException(
                    $"CleanSwitch:RecoveryDataVolumeGptId '{options.RecoveryDataVolumeGptId}' is not a GUID. " +
                    "Expected a value like {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}. " +
                    "Get the GPT unique partition GUID of the volume that should hold the retirement state " +
                    "file by running 'CleanSwitch.exe --list-volumes', or leave the setting empty to fall " +
                    "back to the literal CleanSwitch:RecoveryDataPath.");
            }

            options.RecoveryDataVolumeGptId = VolumeLocator.FormatGptId(gptPartitionId);
        }

        options.Boot1PartitionGptId = NormalizeRequiredGpt(options.Boot1PartitionGptId, "CleanSwitch:Boot1PartitionGptId");
        options.Boot2PartitionGptId = NormalizeRequiredGpt(options.Boot2PartitionGptId, "CleanSwitch:Boot2PartitionGptId");

        if (string.IsNullOrWhiteSpace(options.StateFileName))
        {
            options.StateFileName = CleanSwitchOptions.DefaultStateFileName;
        }

        // Make the effective value explicit so it appears verbatim in logs and errors.
        options.RecoveryDataFolderName = options.ResolveRecoveryDataFolderName();

        return options;
    }

    private static string NormalizeRequiredGpt(string value, string name)
    {
        if (!VolumeLocator.TryParseGptId(value, out var id))
            throw new InvalidOperationException($"{name} must be a concrete GPT partition GUID.");
        return VolumeLocator.FormatGptId(id);
    }

    private sealed class AppSettingsFile
    {
        public CleanSwitchOptions CleanSwitch { get; set; } = new();
    }
}
