using System.Text.Json;

namespace CleanSwitch.Tests;

public sealed class RecoveryDataConfigurationTests
{
    private const string IndependentDataGpt = "{47c8a288-ae3d-4aca-b1ab-d4deceae9d02}";

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.LiveTest.json")]
    public void Source_profiles_pin_independent_partition5_and_keep_override_false(string fileName)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FindRepoFile("CleanSwitch", fileName)));
        var options = json.RootElement.GetProperty("CleanSwitch");

        Assert.Equal(IndependentDataGpt, options.GetProperty("RecoveryDataVolumeGptId").GetString());
        Assert.Equal("CleanSwitchData", options.GetProperty("RecoveryDataFolderName").GetString());
        Assert.Equal(string.Empty, options.GetProperty("LogDirectory").GetString());
        Assert.False(options.GetProperty("AllowStateOnSystemVolume").GetBoolean());
    }

    [Fact]
    public void Gpt_configuration_is_authoritative_and_missing_mount_or_scan_ambiguity_fail_closed()
    {
        var source = File.ReadAllText(FindRepoFile("CleanSwitch", "Services", "RetirementStateStore.cs"));
        var gptBranch = source.IndexOf(
            "if (!string.IsNullOrWhiteSpace(options.RecoveryDataVolumeGptId))",
            StringComparison.Ordinal);
        var gptReturn = source.IndexOf("return ResolveRootByGptId(options, folderName);", StringComparison.Ordinal);
        var literalBranch = source.IndexOf("if (string.IsNullOrWhiteSpace(options.RecoveryDataPath))", StringComparison.Ordinal);

        Assert.True(gptBranch >= 0 && gptReturn > gptBranch && literalBranch > gptReturn);
        Assert.Contains("CleanSwitch will NOT fall back to CleanSwitch:RecoveryDataPath", source, StringComparison.Ordinal);
        Assert.Contains("cannot build a path to it", source, StringComparison.Ordinal);
        Assert.Contains("fixed-volume fallback scanning is disabled", source, StringComparison.Ordinal);
        Assert.Contains("if (candidates.Count > 1)", source, StringComparison.Ordinal);
        Assert.Contains("Ambiguity must stop the flow rather than pick one", source, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(parts[^1]);
    }
}
