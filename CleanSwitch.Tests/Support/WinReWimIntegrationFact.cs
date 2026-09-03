using Xunit;

namespace CleanSwitch.Tests.Support;

[AttributeUsage(AttributeTargets.Method)]
public sealed class WinReWimIntegrationFactAttribute : FactAttribute
{
    public WinReWimIntegrationFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CLEAN_SWITCH_RUN_WINRE_WIM_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set CLEAN_SWITCH_RUN_WINRE_WIM_INTEGRATION=1 and run elevated to exercise disposable DISM WIM servicing.";
        }
    }
}
