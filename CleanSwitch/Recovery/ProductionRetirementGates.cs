namespace CleanSwitch.Recovery;

/// <summary>
/// Compile-time gates for live disk and BCD retirement. The default build keeps both false.
/// Only a build with <c>CleanSwitchLiveTestBuild=true</c> defines
/// <c>CLEANSWITCH_LIVE_TEST_BUILD</c> and flips these on.
/// </summary>
public static class ProductionRetirementGates
{
    public const bool DestructiveOperationsImplemented =
#if CLEANSWITCH_LIVE_TEST_BUILD
        true;
#else
        false;
#endif

    public const bool BcdOperationsImplemented =
#if CLEANSWITCH_LIVE_TEST_BUILD
        true;
#else
        false;
#endif
}
