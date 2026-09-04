using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

/// <summary>
/// Parses <c>bcdedit /enum ... /v</c> text into entries. Shared by the live boot manager
/// and the isolated <c>/store</c> integration test so both use the same rules.
/// </summary>
public static class BcdEditTextParser
{
    public static IReadOnlyList<BcdEntry> Parse(string? output)
    {
        var entries = new List<BcdEntry>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return entries;
        }

        ParsedEntry? current = null;

        foreach (var rawLine in output.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryGetProperty(line, "identifier", out var identifier))
            {
                if (current is not null)
                {
                    entries.Add(ToBcdEntry(current));
                }

                current = new ParsedEntry { Identifier = NormalizeIdentifier(identifier) };
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (TryGetProperty(line, "description", out var description))
            {
                current.Description = description;
            }
            else if (TryGetProperty(line, "path", out var path))
            {
                current.Path = path;
            }
            else
            {
                CaptureExtraProperty(current, line);
            }
        }

        if (current is not null)
        {
            entries.Add(ToBcdEntry(current));
        }

        return entries;
    }

    public static string NormalizeIdentifier(string identifier)
    {
        return Guid.TryParse(identifier.Trim(), out var parsedGuid) && !BcdIdentifiers.IsAlias(identifier)
            ? BcdIdentifiers.Format(parsedGuid)
            : identifier.Trim();
    }

    private static bool TryGetProperty(string line, string name, out string value)
    {
        value = string.Empty;
        if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = line[name.Length..];
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]))
        {
            return false;
        }

        value = rest.Trim();
        return value.Length > 0;
    }

    private static void CaptureExtraProperty(ParsedEntry entry, string line)
    {
        if (TryGetProperty(line, "device", out var device))
        {
            entry.Device = device;
        }
        else if (TryGetProperty(line, "osdevice", out var osDevice))
        {
            entry.OsDevice = osDevice;
        }
        else if (TryGetProperty(line, "recoverysequence", out var recoverySequence))
        {
            entry.RecoverySequence = NormalizeIdentifier(recoverySequence);
        }
        else if (TryGetProperty(line, "resumeobject", out var resumeObject))
        {
            entry.ResumeObject = NormalizeIdentifier(resumeObject);
        }
        else if (TryGetProperty(line, "type", out var type))
        {
            entry.Type = type;
        }
        else if (TryGetProperty(line, "systemroot", out var systemRoot))
        {
            entry.SystemRoot = systemRoot;
        }
    }

    private static BcdEntry ToBcdEntry(ParsedEntry entry) =>
        new(
            entry.Identifier,
            entry.Description.Trim(),
            entry.Path.Trim(),
            entry.Device.Trim(),
            entry.OsDevice.Trim(),
            entry.RecoverySequence.Trim(),
            entry.ResumeObject.Trim(),
            entry.Type.Trim(),
            entry.SystemRoot.Trim());

    private sealed class ParsedEntry
    {
        public string Identifier { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Device { get; set; } = string.Empty;

        public string OsDevice { get; set; } = string.Empty;

        public string RecoverySequence { get; set; } = string.Empty;

        public string ResumeObject { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string SystemRoot { get; set; } = string.Empty;
    }
}
