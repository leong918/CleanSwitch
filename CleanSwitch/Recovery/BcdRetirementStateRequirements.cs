using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// Phase 2C requires schema v2 BCD object GUIDs recorded on Boot 1 before WinRE.
/// Missing IDs are a hard refusal. <see cref="RetirementState.Boot1Id"/> is never
/// copied into the delete command.
/// </summary>
public static class BcdRetirementStateRequirements
{
    public const int RequiredSchemaVersion = 2;

    public const string MustRegenerateMessage =
        "Retirement state is incomplete for Phase 2C and must be regenerated. " +
        "Destructive BCD deletion refuses to infer Boot1BcdObjectId or Boot2BcdObjectId " +
        "from display names, aliases, or legacy Boot1Id/Boot2Id fields inside WinRE. " +
        "Create a new PENDING state with RETIRE SYSTEM on Boot 1 so the concrete BCD " +
        "object GUIDs are recorded before reboot.";

    public static void ValidateForDestructiveExecution(RetirementState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var missing = new List<string>();
        if (state.SchemaVersion < RequiredSchemaVersion)
        {
            missing.Add($"schemaVersion {state.SchemaVersion} (need {RequiredSchemaVersion})");
        }

        var boot1Parsed = BcdIdentifiers.TryParseObjectId(state.Boot1BcdObjectId, out var boot1);
        var boot2Parsed = BcdIdentifiers.TryParseObjectId(state.Boot2BcdObjectId, out var boot2);

        if (!boot1Parsed || BcdIdentifiers.IsProtectedObject(boot1))
        {
            missing.Add("Boot1BcdObjectId (concrete BCD object GUID)");
        }

        if (!boot2Parsed || BcdIdentifiers.IsProtectedObject(boot2))
        {
            missing.Add("Boot2BcdObjectId (concrete BCD object GUID)");
        }

        if (boot1Parsed && boot2Parsed && boot1 == boot2)
        {
            missing.Add("Boot1BcdObjectId and Boot2BcdObjectId must differ");
        }

        if (missing.Count == 0)
        {
            return;
        }

        throw new RetirementExecutionException(
            MustRegenerateMessage +
            Environment.NewLine +
            "Missing or invalid: " + string.Join("; ", missing) +
            Environment.NewLine +
            "No bcdedit command was started.");
    }
}
