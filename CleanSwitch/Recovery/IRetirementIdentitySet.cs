namespace CleanSwitch.Recovery;

/// <summary>
/// Boot 1 / Boot 2 / protected GPT unique ids for one destructive attempt.
/// Production uses <see cref="PinnedRetirementIdentitySet"/> (this PC's NVMe).
/// The VHD integration test injects the ids read from the disposable VHDX.
/// </summary>
public interface IRetirementIdentitySet
{
    Guid Boot1GptId { get; }

    Guid Boot2GptId { get; }

    int? Boot2Disk { get; }

    int? Boot2Partition { get; }

    IReadOnlyList<Guid> ProtectedGptIds { get; }

    string DescribeBoot1();

    string DescribeBoot2();
}

/// <summary>This PC's real GPT pins. Never used by the disposable VHDX test.</summary>
public sealed class PinnedRetirementIdentitySet : IRetirementIdentitySet
{
    public static readonly PinnedRetirementIdentitySet Instance = new();

    private PinnedRetirementIdentitySet()
    {
    }

    public Guid Boot1GptId => PinnedRetirementTargets.Boot1GptId;

    public Guid Boot2GptId => PinnedRetirementTargets.Boot2GptId;

    public int? Boot2Disk => PinnedRetirementTargets.Boot2Disk;

    public int? Boot2Partition => PinnedRetirementTargets.Boot2Partition;

    public IReadOnlyList<Guid> ProtectedGptIds { get; } =
    [
        PinnedRetirementTargets.Boot2GptId,
        Guid.Parse(PinnedRetirementTargets.EfiGpt),
        Guid.Parse(PinnedRetirementTargets.Boot1WinReGpt),
        Guid.Parse(PinnedRetirementTargets.Boot2WinReGpt)
    ];

    public string DescribeBoot1() => PinnedRetirementTargets.DescribeBoot1();

    public string DescribeBoot2() => PinnedRetirementTargets.DescribeBoot2();
}

/// <summary>Run-specific identities, recorded from a live GPT table (for example a VHDX).</summary>
public sealed class RetirementIdentitySet : IRetirementIdentitySet
{
    public required Guid Boot1GptId { get; init; }

    public required Guid Boot2GptId { get; init; }

    public int? Boot2Disk { get; init; }

    public int? Boot2Partition { get; init; }

    public required IReadOnlyList<Guid> ProtectedGptIds { get; init; }

    public string DescribeBoot1() =>
        $"Boot 1 GPT {VolumeLocator.FormatGptId(Boot1GptId)}";

    public string DescribeBoot2() =>
        $"Boot 2 GPT {VolumeLocator.FormatGptId(Boot2GptId)} disk={Boot2Disk?.ToString() ?? "?"} " +
        $"partition={Boot2Partition?.ToString() ?? "?"}";
}
