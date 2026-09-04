using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace CleanSwitch.Tests.Support.Vhd;

internal static class VhdIntegrationCollection
{
    public const string Name = "Disposable VHD integration";
}

[CollectionDefinition(VhdIntegrationCollection.Name, DisableParallelization = true)]
public sealed class VhdIntegrationCollectionDefinition;

/// <summary>
/// Opt-in fact. Normal <c>dotnet test</c> skips this unless CLEANSWITCH_VHD_TESTS=1.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[TraitDiscoverer("CleanSwitch.Tests.Support.Vhd.VhdIntegrationTraitDiscoverer", "CleanSwitch.Tests")]
public sealed class VhdIntegrationFactAttribute : FactAttribute, ITraitAttribute
{
    public VhdIntegrationFactAttribute()
    {
        if (!VhdIntegrationGuard.IsEnabled)
        {
            Skip =
                "Opt-in only. Set CLEANSWITCH_VHD_TESTS=1 to run the disposable VHDX " +
                "destructive integration test. Normal dotnet test skips this.";
        }
    }
}

public sealed class VhdIntegrationTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "VhdIntegration");
    }
}
