using CleanSwitch.Models;
using System.Security.Cryptography;
using System.Text;

namespace CleanSwitch.Recovery;

public static class RetirementExecutionAuthorization
{
    public static void RequireCommitted(RetirementState state, CleanSwitchOptions options) =>
        throw new RetirementExecutionException(
            "Destructive authorization requires an operation token and current WinRE runtime evidence.");

    public static void RequireCommitted(
        RetirementState state,
        CleanSwitchOptions options,
        string? suppliedOperationToken,
        RecoveryRuntimeEvidence runtime)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        if (state.Status is RetirementStatus.Failed or RetirementStatus.Aborted or RetirementStatus.RecoveryRequired)
            throw new RetirementExecutionException(
                $"Automatic destructive execution is forbidden from {RetirementStatusNames.ToWire(state.Status)}.");

        if (!string.Equals(state.HandoffAuthorizationState, HandoffAuthorizationStates.Committed, StringComparison.Ordinal) ||
            !Guid.TryParse(state.HandoffAuthorizationToken, out _) || state.HandoffCommittedAtUtc is null)
            throw new RetirementExecutionException(
                "A durable COMMITTED Phase 2A handoff authorization token is required before destructive execution.");

        if (!Guid.TryParse(suppliedOperationToken, out var supplied) ||
            !Guid.TryParse(state.HandoffAuthorizationToken, out var persisted) ||
            !CryptographicOperations.FixedTimeEquals(supplied.ToByteArray(), persisted.ToByteArray()))
            throw new RetirementExecutionException("The supplied operation token does not match the active committed handoff.");

        if (!string.Equals(ComputeBinding(state), state.HandoffAuthorizationBindingSha256, StringComparison.Ordinal))
            throw new RetirementExecutionException("The committed operation-token binding does not match this handoff instance.");

        var expectedRecovery = BcdIdentifiers.RequireConcreteObjectId(options.RecoveryGuid, "configured recovery");
        var authorizedRecovery = BcdIdentifiers.RequireConcreteObjectId(state.HandoffRecoveryBcdObjectId, "authorized recovery");
        var persistedRecovery = BcdIdentifiers.RequireConcreteObjectId(state.RecoveryId, "persisted recovery");
        if (expectedRecovery != authorizedRecovery || expectedRecovery != persistedRecovery)
            throw new RetirementExecutionException(
                "The committed handoff recovery object does not exactly match the configured and persisted recovery object.");

        if (!runtime.IsWindowsPe || runtime.CurrentResolution != BcdAliasResolution.Resolved ||
            runtime.CurrentBcdObjectId != expectedRecovery)
            throw new RetirementExecutionException(
                "Destructive execution requires WinPE with {current} equal to the exact authorized recovery object.");

        var expectedBoot2 = BcdIdentifiers.RequireConcreteObjectId(options.Boot2Guid, "configured Boot 2");
        var persistedBoot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "persisted Boot 2");
        if (expectedBoot2 != persistedBoot2)
            throw new RetirementExecutionException(
                "The committed handoff does not authorize the configured Boot 2 survivor.");
    }

    public static string ComputeBinding(RetirementState state)
    {
        var canonical = string.Join("\n", state.Operation, state.CreatedAtUtc.ToUniversalTime().ToString("O"),
            state.MachineName, state.Boot1BcdObjectId, state.Boot2BcdObjectId,
            state.HandoffRecoveryBcdObjectId, state.HandoffAuthorizationToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public static class Boot2DefaultInvariant
{
    public static ValidationReport Verify(RetirementState state, BcdSnapshot snapshot, string boundary)
    {
        var report = new ValidationReport($"Boot 2 default invariant at {boundary}");
        var boot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2");
        var matches = snapshot.WithObjectId(boot2);
        var entry = matches.Count == 1 ? matches[0] : null;
        var boot2Gpt = Guid.Empty;
        var hasBoot2Gpt = state.Boot2Identity is not null && state.Boot2Identity.TryGetGptId(out boot2Gpt);
        var deviceMatches = hasBoot2Gpt && entry is not null && BcdDeviceGptResolver.ResolvesTo(entry.Device, boot2Gpt);
        var osDeviceMatches = hasBoot2Gpt && entry is not null && BcdDeviceGptResolver.ResolvesTo(entry.OsDevice, boot2Gpt);
        var loaderMatches = entry is not null && entry.Kind == BcdObjectKind.WindowsLoader &&
                            !entry.Path.Contains("winresume", StringComparison.OrdinalIgnoreCase) &&
                            entry.Path.EndsWith(@"\winload.efi", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.SystemRoot, @"\Windows", StringComparison.OrdinalIgnoreCase);
        var passed = snapshot.DefaultResolution == BcdAliasResolution.Resolved && snapshot.DefaultObjectId == boot2 &&
                     matches.Count == 1 && loaderMatches && deviceMatches && osDeviceMatches;
        report.Add(
            "default-is-exact-boot2",
            passed,
            passed
                ? $"{{default}} is exactly {BcdIdentifiers.Format(boot2)} and Boot 2 is unique."
                : $"{{default}} must be exactly {BcdIdentifiers.Format(boot2)}; observed " +
                  $"{(snapshot.DefaultObjectId is null ? "<unresolved>" : BcdIdentifiers.Format(snapshot.DefaultObjectId.Value))}, " +
                  $"Boot2Matches={matches.Count}; osLoader={loaderMatches}; deviceBoot2Gpt={deviceMatches}; " +
                  $"osdeviceBoot2Gpt={osDeviceMatches}.");
        return report;
    }

    public static void Require(RetirementState state, BcdSnapshot snapshot, string boundary)
    {
        var report = Verify(state, snapshot, boundary);
        if (!report.Passed) throw new RetirementExecutionException(report.Describe());
    }
}

public static class BcdDeviceGptResolver
{
    public static bool ResolvesTo(string value, Guid expected)
    {
        if (BcdIdentifiers.TryParseEmbeddedGuid(value, out var embedded)) return embedded == expected;
        var path = value.Trim();
        if (path.StartsWith("partition=", StringComparison.OrdinalIgnoreCase)) path = path[10..].Trim();
        var matches = VolumeLocator.Enumerate().Volumes.Where(volume =>
            volume.GptPartitionGuid == expected &&
            (string.Equals(volume.VolumeGuidPath.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
             volume.MountPoints.Any(mount => string.Equals(mount.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))).ToList();
        return matches.Count == 1;
    }
}

public static class DestructiveIntentReconciliation
{
    public static List<DestructiveIntentPartitionSnapshot> Capture(GptLayoutSnapshot snapshot) =>
        snapshot.Partitions.Select(partition => new DestructiveIntentPartitionSnapshot
        {
            PartitionGptId = VolumeLocator.FormatGptId(partition.PartitionGptId),
            DiskGptUniqueId = partition.DiskGptId is null ? null : VolumeLocator.FormatGptId(partition.DiskGptId.Value),
            DiskNumber = partition.DiskNumber,
            PartitionNumber = partition.PartitionNumber,
            GptPartitionType = partition.PartitionType is null ? null : VolumeLocator.FormatGptId(partition.PartitionType.Value),
            StartingOffset = partition.StartingOffset,
            SizeBytes = partition.SizeBytes,
            IsRunningSystemVolume = partition.IsRunningSystemVolume
        }).ToList();

    public static ValidationReport VerifyTargetAbsent(RetirementState state, GptLayoutSnapshot live)
    {
        var report = new ValidationReport("Post-destructive intent GPT reconciliation");
        if (state.DestructiveIntentGptSnapshot is not { Count: > 0 } before)
        {
            report.Fail("intent-snapshot", "The durable destructive-intent GPT snapshot is missing.");
            return report;
        }

        var boot1 = RequireGpt(state.Boot1Identity, "Boot 1");
        var boot2 = RequireGpt(state.Boot2Identity, "Boot 2");
        report.Add("boot1-absent", live.WithGptId(boot1).Count == 0,
            $"Boot 1 live GPT matches={live.WithGptId(boot1).Count}; required=0.");
        report.Add("boot2-unique", live.WithGptId(boot2).Count == 1,
            $"Boot 2 live GPT matches={live.WithGptId(boot2).Count}; required=1.");

        var expectedSurvivors = before.Where(item =>
            VolumeLocator.TryParseGptId(item.PartitionGptId, out var id) && id != boot1).ToList();
        foreach (var expected in expectedSurvivors)
        {
            if (!VolumeLocator.TryParseGptId(expected.PartitionGptId, out var id))
            {
                report.Fail("survivor-gpt-valid", $"Invalid persisted GPT id {expected.PartitionGptId}.");
                continue;
            }

            var matches = live.WithGptId(id);
            var exact = matches.Count == 1 && SameGeometry(expected, matches[0]);
            report.Add("survivor-unchanged-" + id.ToString("N"), exact,
                exact ? $"Survivor {expected.PartitionGptId} is unchanged."
                      : $"Survivor {expected.PartitionGptId} is missing, duplicated, or changed.");
        }

        var expectedIds = before.Select(item => item.PartitionGptId)
            .Where(raw => VolumeLocator.TryParseGptId(raw, out _))
            .Select(raw => { VolumeLocator.TryParseGptId(raw, out var id); return id; }).ToHashSet();
        var newIds = live.Partitions.Select(item => item.PartitionGptId).Where(id => !expectedIds.Contains(id)).ToList();
        report.Add("no-new-gpt-partitions", newIds.Count == 0,
            newIds.Count == 0 ? "No new GPT partition appeared." : "Unexpected GPT ids: " + string.Join(", ", newIds));
        return report;
    }

    private static Guid RequireGpt(PartitionIdentity? identity, string role)
    {
        if (identity is null || !identity.TryGetGptId(out var id))
            throw new RetirementExecutionException($"{role} persisted GPT identity is missing or invalid.");
        return id;
    }

    private static bool SameGeometry(DestructiveIntentPartitionSnapshot expected, LivePartition actual) =>
        expected.DiskNumber == actual.DiskNumber && expected.PartitionNumber == actual.PartitionNumber &&
        expected.StartingOffset == actual.StartingOffset && expected.SizeBytes == actual.SizeBytes &&
        SameOptionalGuid(expected.DiskGptUniqueId, actual.DiskGptId) &&
        SameOptionalGuid(expected.GptPartitionType, actual.PartitionType);

    private static bool SameOptionalGuid(string? expected, Guid? actual) =>
        string.IsNullOrWhiteSpace(expected)
            ? actual is null
            : VolumeLocator.TryParseGptId(expected, out var id) && actual == id;
}
