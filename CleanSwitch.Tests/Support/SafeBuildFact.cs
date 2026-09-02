using CleanSwitch.Recovery;
using Xunit;

namespace CleanSwitch.Tests.Support;

/// <summary>Asserts compile against the default safe profile (no CLEANSWITCH_LIVE_TEST_BUILD).</summary>
public sealed class SafeBuildFactAttribute : FactAttribute
{
    public SafeBuildFactAttribute()
    {
        if (ProductionRetirementGates.DestructiveOperationsImplemented ||
            ProductionRetirementGates.BcdOperationsImplemented)
        {
            Skip =
                "Safe-build only. Rebuild without CleanSwitchLiveTestBuild=true to run this test.";
        }
    }
}
