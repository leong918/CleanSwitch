using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// Computes the Boot-1-exclusive BCD object graph from a pre-delete snapshot.
/// Objects in this set may disappear when the Boot 1 partition is removed and must not
/// be required as post-2B survivors.
/// </summary>
public static class BcdBoot1DependencyGraph
{
    public static IReadOnlySet<Guid> ComputeExclusive(
        BcdSnapshot snapshot,
        Guid boot1BcdObjectId,
        Guid boot2BcdObjectId,
        PartitionIdentity? boot1Identity)
    {
        var entriesById = snapshot.Entries
            .Where(entry => !entry.IdentifierWasAlias && entry.ObjectId != Guid.Empty)
            .ToDictionary(entry => entry.ObjectId);

        var exclusive = new HashSet<Guid> { boot1BcdObjectId };
        var queue = new Queue<Guid>();
        queue.Enqueue(boot1BcdObjectId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!entriesById.TryGetValue(current, out var entry))
            {
                continue;
            }

            foreach (var referenced in DirectReferences(entry))
            {
                if (referenced == boot2BcdObjectId || BcdIdentifiers.IsProtectedObject(referenced))
                {
                    continue;
                }

                if (exclusive.Add(referenced))
                {
                    queue.Enqueue(referenced);
                }
            }
        }

        foreach (var entry in snapshot.Entries)
        {
            if (entry.IdentifierWasAlias || entry.ObjectId == Guid.Empty)
            {
                continue;
            }

            if (entry.ObjectId == boot2BcdObjectId || BcdIdentifiers.IsProtectedObject(entry.ObjectId))
            {
                continue;
            }

            if (entry.Kind == BcdObjectKind.ResumeLoader &&
                PointsAtBoot1Volume(entry, boot1Identity) &&
                exclusive.Add(entry.ObjectId))
            {
                queue.Enqueue(entry.ObjectId);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!entriesById.TryGetValue(current, out var chained))
                    {
                        continue;
                    }

                    foreach (var referenced in DirectReferences(chained))
                    {
                        if (referenced == boot2BcdObjectId || BcdIdentifiers.IsProtectedObject(referenced))
                        {
                            continue;
                        }

                        if (exclusive.Add(referenced))
                        {
                            queue.Enqueue(referenced);
                        }
                    }
                }
            }
        }

        return exclusive;
    }

    public static bool PointsAtBoot1Volume(BcdEntryIdentity entry, PartitionIdentity? boot1Identity)
    {
        if (boot1Identity is null)
        {
            return false;
        }

        foreach (var raw in new[] { entry.Device, entry.OsDevice })
        {
            if (ReferencesBoot1(raw, boot1Identity))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlySet<Guid> ResolveExclusiveIds(
        RetirementState state,
        BcdSnapshot after,
        BcdSnapshot? before = null)
    {
        var boot1 = BcdIdentifiers.RequireConcreteObjectId(state.Boot1BcdObjectId, "Boot 1");
        var boot2 = BcdIdentifiers.RequireConcreteObjectId(state.Boot2BcdObjectId, "Boot 2");

        if (state.Boot1ExclusiveBcdObjectIds is { Count: > 0 } persisted)
        {
            return persisted
                .Select(raw => BcdIdentifiers.RequireConcreteObjectId(raw, "Boot 1 exclusive"))
                .ToHashSet();
        }

        if (before is not null)
        {
            return ComputeExclusive(before, boot1, boot2, state.Boot1Identity);
        }

        return InferLegacyExclusive(state, after, boot1);
    }

    private static HashSet<Guid> InferLegacyExclusive(
        RetirementState state,
        BcdSnapshot after,
        Guid boot1BcdObjectId)
    {
        var exclusive = new HashSet<Guid> { boot1BcdObjectId };
        if (state.SurvivorBcdObjectIds is not { Count: > 0 })
        {
            return exclusive;
        }

        var present = after.ConcreteObjectIds();
        foreach (var raw in state.SurvivorBcdObjectIds)
        {
            if (!BcdIdentifiers.TryParseObjectId(raw, out var objectId) || objectId == boot1BcdObjectId)
            {
                continue;
            }

            if (!present.Contains(objectId))
            {
                exclusive.Add(objectId);
            }
        }

        return exclusive;
    }

    private static IEnumerable<Guid> DirectReferences(BcdEntryIdentity entry)
    {
        if (BcdIdentifiers.TryParseObjectId(entry.RecoverySequence, out var recovery))
        {
            yield return recovery;
        }

        if (BcdIdentifiers.TryParseObjectId(entry.ResumeObject, out var resume))
        {
            yield return resume;
        }

        foreach (var raw in new[] { entry.Device, entry.OsDevice })
        {
            if (BcdIdentifiers.TryParseEmbeddedGuid(raw, out var embedded))
            {
                yield return embedded;
            }
        }
    }

    private static bool ReferencesBoot1(string? raw, PartitionIdentity boot1Identity)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (BcdIdentifiers.TryParseEmbeddedGuid(raw, out var embedded) &&
            VolumeLocator.TryParseGptId(boot1Identity.GptPartitionId, out var boot1Gpt) &&
            embedded == boot1Gpt)
        {
            return true;
        }

        var letter = boot1Identity.ObservedDriveLetter?.Trim();
        if (string.IsNullOrWhiteSpace(letter))
        {
            return false;
        }

        var normalized = letter.TrimEnd('\\');
        if (!normalized.EndsWith(':'))
        {
            normalized += ":";
        }

        return raw.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }
}
