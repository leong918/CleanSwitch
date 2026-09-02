using CleanSwitch.Recovery;
using Xunit;

namespace CleanSwitch.Tests.Support;

/// <summary>Asserts compile against the explicit live-test profile.</summary>
public sealed class LiveTestBuildFactAttribute : FactAttribute
{
    public LiveTestBuildFactAttribute()
    {
        if (!ProductionRetirementGates.DestructiveOperationsImplemented ||
            !ProductionRetirementGates.BcdOperationsImplemented)
        {
            Skip =
                "Live-test profile only. Rebuild with /p:CleanSwitchLiveTestBuild=true to run this test.";
        }
    }
}
