using System.Text.RegularExpressions;

namespace CleanSwitch.Recovery;

public sealed record BcdBootSequenceParseResult(
    bool Confident,
    bool PropertyPresent,
    IReadOnlyList<string> Identifiers,
    string Diagnostic);

public static class BcdBootSequenceParser
{
    private static readonly HashSet<string> KnownProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "identifier", "device", "description", "locale", "inherit", "flightsigning",
        "default", "resumeobject", "displayorder", "toolsdisplayorder", "timeout",
        "bootsequence", "bootems", "customactions"
    };

    public static BcdBootSequenceParseResult Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Failed("BCDEdit returned empty output.");
        var identifiers = new List<string>();
        var sawSeparator = false;
        var sawIdentifier = false;
        var sawKnownProperty = false;
        var inSequence = false;
        var acceptsGuidContinuation = false;
        var present = false;
        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { inSequence = false; acceptsGuidContinuation = false; continue; }
            if (!sawSeparator)
            {
                if (Regex.IsMatch(line, "^-{3,}$")) sawSeparator = true;
                continue;
            }
            if (acceptsGuidContinuation && Regex.IsMatch(line, "^\\{[0-9a-fA-F-]{36}\\}$"))
            {
                if (inSequence) AddIds(line, identifiers);
                continue;
            }
            var match = Regex.Match(line, "^(?<key>[A-Za-z][A-Za-z0-9]*)\\s+(?<value>.*)$");
            if (!match.Success || !KnownProperties.Contains(match.Groups["key"].Value))
                return Failed($"Unrecognized boot-manager output line: '{line}'.");
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value;
            sawKnownProperty = true;
            inSequence = key.Equals("bootsequence", StringComparison.OrdinalIgnoreCase);
            acceptsGuidContinuation = inSequence ||
                key.Equals("displayorder", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("toolsdisplayorder", StringComparison.OrdinalIgnoreCase);
            if (key.Equals("identifier", StringComparison.OrdinalIgnoreCase))
            {
                sawIdentifier = BcdIdentifiers.TryParseObjectId(value, out var id) && id == BcdIdentifiers.BootManagerId;
                if (!sawIdentifier) return Failed("Output is not the exact boot-manager object.");
            }
            if (inSequence)
            {
                present = true;
                AddIds(value, identifiers);
            }
        }
        if (!sawSeparator || !sawIdentifier || !sawKnownProperty)
            return Failed("Boot-manager output could not be positively identified.");
        return new BcdBootSequenceParseResult(true, present, identifiers,
            "Boot-manager output parsed with positive confidence.");
    }

    private static void AddIds(string value, List<string> ids)
    {
        foreach (Match match in Regex.Matches(value, "\\{[0-9a-fA-F-]{36}\\}"))
            if (BcdIdentifiers.TryParseObjectId(match.Value, out var id)) ids.Add(BcdIdentifiers.Format(id));
    }

    private static BcdBootSequenceParseResult Failed(string diagnostic) => new(false, false, [], diagnostic);
}
