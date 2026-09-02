using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using Xunit;

namespace CleanSwitch.Tests;

public sealed class RetirementExecutorSafetyTests
{
    [SafeBuildFact]
    public void Production_flags_stay_disabled()
    {
        var executor = new RetirementExecutor(
            RetirementFixtures.Options(enableDestructive: false),
            new RecordingOperationLog(),
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            new FakeDestructiveDiskCommand());

        Assert.False(executor.IsDestructiveRetirementAvailable);
        Assert.False(executor.IsBcdRetirementAvailable);
        Assert.False(executor.IsConfigEnabled);
    }

    [Fact]
    public void Appsettings_does_not_enable_destructive_retirement()
    {
        var path = FindRepoAppsettings();
        Assert.True(File.Exists(path), path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"EnableDestructiveRetirement\": false", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EnableDestructiveRetirement\": true", json, StringComparison.Ordinal);
    }

    [SafeBuildFact]
    public async Task RetireBoot1Async_never_calls_command_while_implemented_flag_is_false()
    {
        var command = new FakeDestructiveDiskCommand();
        var executor = new RetirementExecutor(
            RetirementFixtures.Options(enableDestructive: true),
            new RecordingOperationLog(),
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            command);

        var exception = await Assert.ThrowsAsync<RetirementNotImplementedException>(() =>
            executor.RetireBoot1Async(
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity(),
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true));

        Assert.Contains("DestructiveOperationsImplemented is false", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.ExecuteCount);
        Assert.Null(command.LastTarget);
    }

    [SafeBuildFact]
    public void BuildDeletionPlan_is_non_destructive_even_when_opt_in_is_set()
    {
        var command = new FakeDestructiveDiskCommand();
        var executor = new RetirementExecutor(
            RetirementFixtures.Options(enableDestructive: true),
            new RecordingOperationLog(),
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            command);

        var plan = executor.BuildDeletionPlan(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.PassingValidation(),
            explicitOptIn: true);

        Assert.True(plan.TargetIdentified);
        Assert.False(plan.ExecutionAuthorised);
        Assert.Equal(0, command.ExecuteCount);
    }

    private static string FindRepoAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CleanSwitch", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
    }
}
