using CleanSwitch.Recovery;

namespace CleanSwitch.Tests;

public sealed class BcdIdentifiersTests
{
    [Theory]
    [InlineData("{current}")]
    [InlineData("{default}")]
    [InlineData("{bootmgr}")]
    [InlineData("Windows 11")]
    [InlineData("Boot 1")]
    [InlineData("C:")]
    public void Aliases_and_display_names_are_not_concrete_object_ids(string raw)
    {
        Assert.False(BcdIdentifiers.TryParseObjectId(raw, out _));
        var exception = Assert.Throws<RetirementExecutionException>(() =>
            BcdIdentifiers.RequireConcreteObjectId(raw, "Boot 1"));
        Assert.Contains("concrete BCD object GUID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Concrete_guid_is_accepted_and_formatted()
    {
        var guid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
        Assert.True(BcdIdentifiers.TryParseObjectId("{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAA1}", out var parsed));
        Assert.Equal(guid, parsed);
        Assert.Equal("{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1}", BcdIdentifiers.Format(parsed));
    }

    [Theory]
    [InlineData("partition={eab2ae6c-4d1b-4181-873c-3b8f06a1e465}", "eab2ae6c-4d1b-4181-873c-3b8f06a1e465")]
    [InlineData("{4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb}", "4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb")]
    public void Embedded_partition_guid_is_parsed(string text, string expected)
    {
        Assert.True(BcdIdentifiers.TryParseEmbeddedGuid(text, out var parsed));
        Assert.Equal(Guid.Parse(expected), parsed);
    }

    [Theory]
    [InlineData("partition=C:")]
    [InlineData(@"partition=\Device\HarddiskVolume3")]
    [InlineData("unknown")]
    [InlineData("{current}")]
    [InlineData("Windows 11")]
    public void Letters_aliases_and_display_names_are_not_embedded_guids(string text)
    {
        Assert.False(BcdIdentifiers.TryParseEmbeddedGuid(text, out _));
    }

    [Fact]
    public void Bootmgr_guid_is_protected()
    {
        Assert.True(BcdIdentifiers.IsProtectedObject(BcdIdentifiers.BootManagerId));
        Assert.Throws<RetirementExecutionException>(() =>
            BcdIdentifiers.RequireConcreteObjectId(BcdIdentifiers.Format(BcdIdentifiers.BootManagerId), "Boot 1"));
    }
}
