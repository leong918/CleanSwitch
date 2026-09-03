namespace CleanSwitch.Tests.Support;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class WinReDeploymentVmIntegrationFactAttribute : FactAttribute
{
    public WinReDeploymentVmIntegrationFactAttribute()
    {
        var enabled = Environment.GetEnvironmentVariable("CLEAN_SWITCH_RUN_WINRE_DEPLOYMENT_VM_INTEGRATION");
        var harness = Environment.GetEnvironmentVariable("CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS");
        if (!string.Equals(enabled, "1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(harness) || !File.Exists(harness))
        {
            Skip = "Requires an explicitly supplied disposable-VM deployment harness; never runs against the host.";
        }
    }
}
