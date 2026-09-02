namespace CleanSwitch.Tests.Support.Vhd;

internal static class VhdIntegrationGuard
{
    public const string EnvironmentVariable = "CLEANSWITCH_VHD_TESTS";

    public static bool IsEnabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return raw is "1" or "true" or "TRUE" or "yes" or "YES";
        }
    }
}
