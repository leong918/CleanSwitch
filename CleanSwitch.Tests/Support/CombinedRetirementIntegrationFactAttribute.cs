using CleanSwitch.Tests.Support.Bcd;
using CleanSwitch.Tests.Support.Vhd;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace CleanSwitch.Tests.Support;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[TraitDiscoverer("CleanSwitch.Tests.Support.CombinedRetirementIntegrationTraitDiscoverer", "CleanSwitch.Tests")]
public sealed class CombinedRetirementIntegrationFactAttribute : FactAttribute, ITraitAttribute
{
    public CombinedRetirementIntegrationFactAttribute()
    {
        if (!VhdIntegrationGuard.IsEnabled || !BcdIntegrationGuard.IsEnabled)
        {
            Skip = "Opt-in only. Set both CLEANSWITCH_VHD_TESTS=1 and CLEANSWITCH_BCD_TESTS=1.";
        }
    }
}

public sealed class CombinedRetirementIntegrationTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "CombinedRetirementIntegration");
    }
}
