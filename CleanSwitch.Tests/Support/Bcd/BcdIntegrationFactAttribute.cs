using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace CleanSwitch.Tests.Support.Bcd;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[TraitDiscoverer("CleanSwitch.Tests.Support.Bcd.BcdIntegrationTraitDiscoverer", "CleanSwitch.Tests")]
public sealed class BcdIntegrationFactAttribute : FactAttribute, ITraitAttribute
{
    public BcdIntegrationFactAttribute()
    {
        if (!BcdIntegrationGuard.IsEnabled)
        {
            Skip =
                "Opt-in only. Set CLEANSWITCH_BCD_TESTS=1 to run the isolated bcdedit /store test. " +
                "Normal dotnet test skips this. The system BCD is never opened.";
        }
    }
}

public sealed class BcdIntegrationTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "BcdIntegration");
    }
}
