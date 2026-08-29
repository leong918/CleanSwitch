using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Thrown by every destructive path in <see cref="RetirementExecutor"/>. The type exists so
/// callers cannot mistake "not implemented" for "nothing to do".
/// </summary>
public sealed class RetirementNotImplementedException : Exception
{
    public RetirementNotImplementedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// ============================================================================
/// NOT IMPLEMENTED — DESTRUCTIVE PHASE 2B COMPONENT
/// ============================================================================
/// This class is the only place that will ever be allowed to remove Boot 1. Nothing in it
/// is implemented. Every entry point throws.
///
/// Phase 2A never calls it: <see cref="RecoveryRunner"/> skips deletion entirely and goes
/// straight from Boot 2 validation to the BCD handoff.
///
/// Three independent guards must all be satisfied before any real work could run, and the
/// first one is a hard-coded flag that is <c>false</c> in this build:
///   1. <see cref="DestructiveOperationsImplemented"/> is false, so the method throws
///      before looking at anything else.
///   2. The caller must pass <c>explicitOptIn: true</c>.
///   3. <c>CleanSwitch:EnableDestructiveRetirement</c> must be true in appsettings.json.
/// A careless future call that forgets any of these cannot proceed.
/// ============================================================================
/// </summary>
public sealed class RetirementExecutor
{
    /// <summary>
    /// Hard switch for the destructive implementation. Stays false until Phase 2B is written,
    /// reviewed and tested on a machine that is expendable.
    /// </summary>
    private static readonly bool DestructiveOperationsImplemented = false;

    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;

    public RetirementExecutor(CleanSwitchOptions options, IOperationLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
    }

    /// <summary>
    /// Reports whether a destructive run could even be attempted. Phase 2A logs this and
    /// moves on; it is deliberately not a permission to act.
    /// </summary>
    public bool IsDestructiveRetirementAvailable => DestructiveOperationsImplemented;

    /// <summary>
    /// NOT IMPLEMENTED. Always throws <see cref="RetirementNotImplementedException"/>.
    /// </summary>
    /// <param name="target">Stable identity of the partition Phase 2B would retire.</param>
    /// <param name="validation">Report from <see cref="DiskValidator.ValidateRetirementTarget"/>.</param>
    /// <param name="explicitOptIn">Must be true. Present so a call site cannot be accidental.</param>
    public Task RetireBoot1Async(
        PartitionIdentity target,
        ValidationReport validation,
        bool explicitOptIn)
    {
        _log.Warn(
            "executor",
            "RetireBoot1Async was called. Phase 2B is NOT IMPLEMENTED, so this call is refused. " +
            $"target=[{target?.Describe() ?? "<null>"}] validationPassed={validation?.Passed ?? false} " +
            $"explicitOptIn={explicitOptIn} enableDestructiveRetirement={_options.EnableDestructiveRetirement}");

        // Guard 1: the implementation does not exist in this build. Checked first so that no
        // configuration or argument combination can reach a destructive code path.
        if (!DestructiveOperationsImplemented)
        {
            throw new RetirementNotImplementedException(
                "NOT IMPLEMENTED: Boot 1 retirement (partition deletion / BCD entry removal) is not part of " +
                "Phase 2A." +
                Environment.NewLine +
                "No disk was touched. RetirementExecutor contains no deletion code at all: there is nothing " +
                "here that could partially delete a partition." +
                Environment.NewLine +
                "Phase 2B must implement this behind DiskValidator.ValidateRetirementTarget passing, and must " +
                "flip RetirementExecutor.DestructiveOperationsImplemented deliberately.");
        }

        // Guard 2 and 3 are unreachable in this build and exist so the shape of the
        // authorisation is reviewable now, before any deletion code is written.
        if (!explicitOptIn)
        {
            throw new RetirementNotImplementedException(
                "Refusing to retire Boot 1: the caller did not pass explicitOptIn.");
        }

        if (!_options.EnableDestructiveRetirement)
        {
            throw new RetirementNotImplementedException(
                "Refusing to retire Boot 1: CleanSwitch:EnableDestructiveRetirement is not true.");
        }

        if (validation is null || !validation.Passed)
        {
            throw new RetirementNotImplementedException(
                "Refusing to retire Boot 1: the retirement target did not pass validation.");
        }

        throw new RetirementNotImplementedException(
            "Refusing to retire Boot 1: no deletion implementation exists.");
    }

    /// <summary>
    /// NOT IMPLEMENTED. Phase 2C would remove the Boot 1 loader object from the BCD store
    /// after the partition has gone. Always throws.
    /// </summary>
    public Task DeleteBoot1BcdEntryAsync(string boot1Guid, bool explicitOptIn)
    {
        _log.Warn(
            "executor",
            $"DeleteBoot1BcdEntryAsync was called for {boot1Guid} (explicitOptIn={explicitOptIn}). " +
            "Phase 2C is NOT IMPLEMENTED; refused.");

        throw new RetirementNotImplementedException(
            "NOT IMPLEMENTED: removing the Boot 1 BCD entry ('bcdedit /delete') is Phase 2C work. " +
            "No BCD object was deleted.");
    }
}
