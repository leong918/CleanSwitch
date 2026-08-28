using System.Diagnostics;
using System.Globalization;
using System.Text;
using CleanSwitch.Models;

namespace CleanSwitch.Services;

public sealed class WindowsBootManager : IBootManager
{
    public async Task<BootLayout> DetectAsync(string? preferredOtherGuid)
    {
        var currentOutput = await ReadBcdEditOutputAsync(["/enum", "{current}", "/v"]);
        var loaderOutput = await ReadBcdEditOutputAsync(["/enum", "OSLOADER", "/v"]);

        var currentEntries = ParseEntries(currentOutput);
        var current = currentEntries.FirstOrDefault(IsSwitchableWindows)
            ?? currentEntries.FirstOrDefault(entry => Guid.TryParse(entry.Identifier, out _))
            ?? throw new BootManagerException(
                "Could not detect the currently running Windows boot entry from BCDEdit.");

        var loaders = ParseEntries(loaderOutput)
            .Where(IsSwitchableWindows)
            .GroupBy(entry => entry.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var currentFromLoaders = loaders.FirstOrDefault(entry => IdsEqual(entry.Identifier, current.Identifier));
        if (currentFromLoaders is not null)
        {
            current = currentFromLoaders;
        }
        else
        {
            loaders.Add(current);
        }

        var others = loaders
            .Where(entry => !IdsEqual(entry.Identifier, current.Identifier))
            .ToList();

        var target = SelectTarget(current, others, preferredOtherGuid);
        return new BootLayout(
            ToBootEntry(current),
            ToBootEntry(target));
    }

    public async Task<bool> SetNextBootAsync(string bootGuid)
    {
        var normalizedGuid = NormalizeBootGuid(bootGuid);

        Trace.WriteLine($"Validating that target BCD identifier {normalizedGuid} exists.");
        var enumeration = await RunProcessAsync(
            "bcdedit.exe",
            ["/enum", normalizedGuid, "/v"]);

        if (enumeration.ExitCode != 0)
        {
            throw new BootManagerException(
                $"Target BCD identifier {normalizedGuid} was not found or could not be read." +
                FormatProcessOutput(enumeration));
        }

        Trace.WriteLine($"Setting {normalizedGuid} as the next one-time boot target.");
        var bootSequence = await RunProcessAsync(
            "bcdedit.exe",
            ["/bootsequence", normalizedGuid]);

        if (bootSequence.ExitCode != 0)
        {
            Trace.WriteLine($"BCDEdit failed while setting the one-time boot target. ExitCode={bootSequence.ExitCode}.");
            throw new BootManagerException(
                $"BCDEdit failed while setting the one-time boot target. The computer will not be restarted." +
                FormatProcessOutput(bootSequence));
        }

        Trace.WriteLine($"BCDEdit successfully set the next one-time boot target to {normalizedGuid}.");
        return true;
    }

    public async Task RestartAsync(int delaySeconds)
    {
        if (delaySeconds < 0)
        {
            throw new BootManagerException("RestartDelaySeconds must be zero or greater.");
        }

        Trace.WriteLine($"Scheduling a restart in {delaySeconds} seconds.");
        var restart = await RunProcessAsync(
            "shutdown.exe",
            ["/r", "/t", delaySeconds.ToString(CultureInfo.InvariantCulture)]);

        if (restart.ExitCode != 0)
        {
            throw new BootManagerException(
                $"Windows shutdown.exe failed." + FormatProcessOutput(restart));
        }

        Trace.WriteLine("Windows restart successfully scheduled.");
    }

    private static ParsedEntry SelectTarget(
        ParsedEntry current,
        IReadOnlyList<ParsedEntry> others,
        string? preferredOtherGuid)
    {
        if (others.Count == 0)
        {
            throw new BootManagerException(
                $"Only one Windows boot entry was found: {FormatEntry(current)}. " +
                "CleanSwitch needs a second Windows Boot Loader entry to switch to.");
        }

        if (others.Count == 1)
        {
            return others[0];
        }

        if (!string.IsNullOrWhiteSpace(preferredOtherGuid) &&
            Guid.TryParse(preferredOtherGuid.Trim(), out var preferredGuid))
        {
            var preferredId = FormatGuid(preferredGuid);
            if (!IdsEqual(preferredId, current.Identifier))
            {
                return others.FirstOrDefault(entry => IdsEqual(entry.Identifier, preferredId))
                    ?? throw new BootManagerException(
                        $"Configured Boot2Guid {preferredId} was not found among the other Windows boot entries.");
            }
        }

        var named = others.FirstOrDefault(entry =>
            ContainsIgnoreCase(entry.Description, "Boot 1") ||
            ContainsIgnoreCase(entry.Description, "Main") ||
            ContainsIgnoreCase(entry.Description, "Clean"));
        if (named is not null)
        {
            return named;
        }

        var found = string.Join(", ", others.Select(FormatEntry));
        throw new BootManagerException(
            "Multiple other Windows boot entries were found. Set CleanSwitch:Boot2Guid in appsettings.json to choose one. Found: " +
            found);
    }

    private async Task<string> ReadBcdEditOutputAsync(string[] arguments)
    {
        ProcessResult? lastFailure = null;
        foreach (var encoding in new[] { Encoding.Unicode, Encoding.UTF8, Encoding.Default })
        {
            var result = await RunProcessAsync("bcdedit.exe", arguments, encoding);
            if (result.ExitCode == 0 && ParseEntries(result.StdOut).Count > 0)
            {
                return result.StdOut;
            }

            lastFailure = result;
        }

        if (lastFailure is null || lastFailure.ExitCode != 0)
        {
            throw new BootManagerException(
                "BCDEdit could not enumerate boot entries." +
                (lastFailure is null ? string.Empty : FormatProcessOutput(lastFailure)));
        }

        throw new BootManagerException(
            "BCDEdit ran, but no Windows boot entries could be parsed from its output.");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        Encoding? encoding = null)
    {
        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Trace.WriteLine($"Executing {fileName} with arguments: {string.Join(" ", argumentList)}.");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new BootManagerException($"Could not start {fileName}.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new BootManagerException($"Could not start {fileName}: {exception.Message}", exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var result = new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);

        Trace.WriteLine(
            $"{fileName} completed. ExitCode={result.ExitCode}, " +
            $"StdOut={FormatForLog(result.StdOut)}, StdErr={FormatForLog(result.StdErr)}.");

        return result;
    }

    private static List<ParsedEntry> ParseEntries(string output)
    {
        var entries = new List<ParsedEntry>();
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
                    entries.Add(current);
                }

                current = new ParsedEntry
                {
                    Identifier = NormalizeIdentifier(identifier)
                };
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
        }

        if (current is not null)
        {
            entries.Add(current);
        }

        return entries;
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

    private static bool IsSwitchableWindows(ParsedEntry entry)
    {
        if (!Guid.TryParse(entry.Identifier, out _))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.Path) &&
            entry.Path.Contains("winresume", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.Path) &&
            !entry.Path.Contains("winload", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ContainsIgnoreCase(entry.Description, "Recovery");
    }

    private static string NormalizeIdentifier(string identifier)
    {
        return Guid.TryParse(identifier.Trim(), out var parsedGuid)
            ? FormatGuid(parsedGuid)
            : identifier.Trim();
    }

    private static string NormalizeBootGuid(string bootGuid)
    {
        if (string.IsNullOrWhiteSpace(bootGuid) ||
            !Guid.TryParse(bootGuid.Trim(), out var parsedGuid))
        {
            throw new BootManagerException(
                $"The target boot identifier is not a valid Windows BCD GUID. Received '{bootGuid}'.");
        }

        return FormatGuid(parsedGuid);
    }

    private static BootEntry ToBootEntry(ParsedEntry entry)
    {
        var description = string.IsNullOrWhiteSpace(entry.Description)
            ? $"Windows {entry.Identifier}"
            : entry.Description.Trim();
        return new BootEntry(entry.Identifier, description);
    }

    private static string FormatEntry(ParsedEntry entry) =>
        $"{(string.IsNullOrWhiteSpace(entry.Description) ? "Windows" : entry.Description)} ({entry.Identifier})";

    private static string FormatGuid(Guid guid) => $"{{{guid:D}}}";

    private static bool IdsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string? value, string part) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static string FormatProcessOutput(ProcessResult result)
    {
        var stdout = string.IsNullOrWhiteSpace(result.StdOut) ? "<empty>" : result.StdOut.Trim();
        var stderr = string.IsNullOrWhiteSpace(result.StdErr) ? "<empty>" : result.StdErr.Trim();
        return $"{Environment.NewLine}{Environment.NewLine}Exit code: {result.ExitCode}{Environment.NewLine}Output: {stdout}{Environment.NewLine}Error: {stderr}";
    }

    private static string FormatForLog(string value) =>
        string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();

    private sealed class ParsedEntry
    {
        public string Identifier { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}

public sealed class BootManagerException : Exception
{
    public BootManagerException(string message)
        : base(message)
    {
    }

    public BootManagerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
