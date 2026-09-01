namespace CleanSwitch.Tests.Support.Bcd;

internal static class BcdIntegrationGuard
{
    public const string EnvironmentVariable = "CLEANSWITCH_BCD_TESTS";

    public static bool IsEnabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return raw is "1" or "true" or "TRUE" or "yes" or "YES";
        }
    }
}
