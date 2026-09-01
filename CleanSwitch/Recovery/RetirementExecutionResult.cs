namespace CleanSwitch.Recovery;

public enum RetirementExecutionKind
{
    /// <summary>diskpart ran and Boot 1's GPT unique id is now absent.</summary>
    Succeeded,

    /// <summary>Boot 1 GPT was already absent and Boot 2 was still present. diskpart was not started.</summary>
    AlreadyGone,

    /// <summary>State already recorded a deletion. diskpart was not started.</summary>
    AlreadyRecorded
}

public sealed class RetirementExecutionResult
{
    public required RetirementExecutionKind Kind { get; init; }

    /// <summary>True only when this invocation actually sent <c>delete partition override</c>.</summary>
    public required bool DestructiveDeletionOccurred { get; init; }

    public required string Message { get; init; }

    public RetirementDeletionPlan? Plan { get; init; }
}

/// <summary>Live deletion was attempted and refused or failed. No successful delete is implied.</summary>
public sealed class RetirementExecutionException : Exception
{
    public RetirementExecutionException(string message)
        : base(message)
    {
    }

    public RetirementExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
