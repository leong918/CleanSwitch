using System.Diagnostics;
using System.Text.RegularExpressions;
using CleanSwitch.Recovery;
using CleanSwitch.Services;

namespace CleanSwitch.Tests.Support.Bcd;

/// <summary>
/// Temporary BCD created with <c>bcdedit /createstore</c>. Every later command uses
/// <c>/store</c> on that file. The system BCD is never named and <c>/delete</c>
/// without <c>/store</c> is refused.
/// </summary>
internal sealed class IsolatedBcdStoreSession : IDisposable
{
    private IsolatedBcdStoreSession(string storePath)
    {
        StorePath = CanonicalizeTempStorePath(storePath);
    }

    public string StorePath { get; }

    public Guid Boot1Id { get; private set; }

    public Guid Boot2Id { get; private set; }

    public Guid RecoveryId { get; private set; }

    public Guid ExtraId { get; private set; }

    public static IsolatedBcdStoreSession Create()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"cleanswitch-bcd-{Guid.NewGuid():N}.bcd"));
        var session = new IsolatedBcdStoreSession(path);
        try
        {
            RefuseIfSystemStore(session.StorePath);
            RunBcdedit("/createstore", session.StorePath);
            RunBcdedit("/store", session.StorePath, "/create", "{bootmgr}", "/d", "Windows Boot Manager");
            session.Boot1Id = CreateLoader(session.StorePath, "Fake Boot 1");
            session.Boot2Id = CreateLoader(session.StorePath, "Fake Boot 2");
            session.RecoveryId = CreateLoader(session.StorePath, "Windows Recovery Environment");
            session.ExtraId = CreateLoader(session.StorePath, "Unrelated test loader");
            if (session.Boot1Id == session.Boot2Id ||
                session.Boot1Id == session.ExtraId ||
                session.Boot2Id == session.ExtraId)
            {
                throw new InvalidOperationException("Isolated BCD loader GUIDs were not distinct. Refusing.");
            }

            RunBcdedit("/store", session.StorePath, "/set", "{bootmgr}", "default", BcdIdentifiers.Format(session.Boot2Id));
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public IBcdStoreSource CreateStoreSource() => new BcdeditFileStoreSource(StorePath);

    public IDestructiveBcdCommand CreateBoundCommand(IOperationLog? log = null) =>
        new StoreBoundBcdCommand(StorePath, log);

    public void Dispose()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                File.Delete(StorePath);
            }
        }
        catch (IOException)
        {
        }
    }

    public static string CanonicalizeTempStorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Isolated BCD store path is empty.");
        }

        var full = Path.GetFullPath(path);
        RefuseIfSystemStore(full);
        RequireUnderTemp(full);
        return full;
    }

    public static void RefuseIfSystemStore(string path)
    {
        var full = Path.GetFullPath(path);
        var fileName = Path.GetFileName(full);
        if (full.Contains(@"\EFI\Microsoft\Boot\BCD", StringComparison.OrdinalIgnoreCase) ||
            full.EndsWith(@"\Boot\BCD", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(fileName, "BCD", StringComparison.OrdinalIgnoreCase) &&
             full.Contains(@"\Microsoft\Boot\", StringComparison.OrdinalIgnoreCase)) ||
            LooksLikeWindowsBootBcd(full))
        {
            throw new InvalidOperationException(
                $"Refusing to use '{full}' as an isolated BCD store. That path looks like the system BCD.");
        }
    }

    private static bool LooksLikeWindowsBootBcd(string fullPath)
    {
        try
        {
            var systemBoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Boot", "BCD"));
            if (string.Equals(fullPath, systemBoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static void RequireUnderTemp(string fullPath)
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Isolated BCD store '{fullPath}' is not under the temp directory '{temp}'. Refusing.");
        }
    }

    private static Guid CreateLoader(string store, string description)
    {
        var output = RunBcdedit("/store", store, "/create", "/d", description, "/application", "osloader");
        var match = Regex.Match(output, @"\{[0-9a-fA-F-]{36}\}");
        if (!match.Success || !BcdIdentifiers.TryParseObjectId(match.Value, out var id))
        {
            throw new InvalidOperationException("bcdedit /create did not return a concrete GUID. Output: " + output);
        }

        RunBcdedit("/store", store, "/set", BcdIdentifiers.Format(id), "path", @"\Windows\system32\winload.efi");
        return id;
    }

    internal static string RunBcdedit(params string[] arguments)
    {
        RefuseUnsafeBcdedit(arguments);

        var start = new ProcessStartInfo
        {
            FileName = "bcdedit.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("bcdedit.exe failed to start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("bcdedit.exe exceeded 60 seconds.");
        }

        var output = (stdout + Environment.NewLine + stderr).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "bcdedit failed: " + string.Join(' ', arguments) + Environment.NewLine + output);
        }

        return output;
    }

    internal static void RefuseUnsafeBcdedit(IReadOnlyList<string> arguments)
    {
        var hasDelete = arguments.Any(argument => argument.Equals("/delete", StringComparison.OrdinalIgnoreCase));
        var hasStore = arguments.Any(argument => argument.Equals("/store", StringComparison.OrdinalIgnoreCase));
        var hasCreateStore = arguments.Any(argument => argument.Equals("/createstore", StringComparison.OrdinalIgnoreCase));

        if (hasDelete && !hasStore)
        {
            throw new InvalidOperationException(
                "Isolated BCD harness refused bcdedit /delete without /store.");
        }

        if (!hasCreateStore && !hasStore)
        {
            throw new InvalidOperationException(
                "Isolated BCD harness requires /store on every bcdedit command except /createstore.");
        }

        if (hasStore)
        {
            var index = arguments.ToList().FindIndex(argument =>
                argument.Equals("/store", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= arguments.Count)
            {
                throw new InvalidOperationException("bcdedit /store is missing its path argument.");
            }

            CanonicalizeTempStorePath(arguments[index + 1]);
        }

        if (hasCreateStore)
        {
            var index = arguments.ToList().FindIndex(argument =>
                argument.Equals("/createstore", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= arguments.Count)
            {
                throw new InvalidOperationException("bcdedit /createstore is missing its path argument.");
            }

            CanonicalizeTempStorePath(arguments[index + 1]);
        }
    }
}

internal sealed class BcdeditFileStoreSource : IBcdStoreSource
{
    private readonly string _storePath;

    public BcdeditFileStoreSource(string storePath)
    {
        _storePath = IsolatedBcdStoreSession.CanonicalizeTempStorePath(storePath);
    }

    public Task<BcdSnapshot> CaptureAsync()
    {
        var all = BcdEditTextParser.Parse(
            IsolatedBcdStoreSession.RunBcdedit("/store", _storePath, "/enum", "all", "/v"));
        var identities = all.Select(BcdEntryIdentity.FromEntry).ToList();
        Guid? defaultId = null;
        try
        {
            var defaultEntries = BcdEditTextParser.Parse(
                IsolatedBcdStoreSession.RunBcdedit("/store", _storePath, "/enum", "{default}", "/v"));
            defaultId = defaultEntries
                .Select(BcdEntryIdentity.FromEntry)
                .Where(entry => !entry.IdentifierWasAlias && entry.ObjectId != Guid.Empty)
                .Select(entry => entry.ObjectId)
                .Distinct()
                .SingleOrDefault();
            if (defaultId == Guid.Empty)
            {
                defaultId = null;
            }
        }
        catch (InvalidOperationException)
        {
        }

        var bootmgr = identities.Any(entry => entry.ObjectId == BcdIdentifiers.BootManagerId);
        return Task.FromResult(new BcdSnapshot(
            identities,
            currentObjectId: null,
            defaultId,
            bootmgr,
            [],
            currentResolution: BcdAliasResolution.Absent,
            defaultResolution: defaultId is null
                ? BcdAliasResolution.Unresolved
                : BcdAliasResolution.Resolved));
    }
}

internal sealed class StoreBoundBcdCommand : IDestructiveBcdCommand
{
    private readonly BcdeditDestructiveBcdCommand _inner;
    private readonly string _storePath;
    private readonly IOperationLog _log;

    public StoreBoundBcdCommand(string storePath, IOperationLog? log = null)
    {
        _storePath = IsolatedBcdStoreSession.CanonicalizeTempStorePath(storePath);
        _log = log ?? NullOperationLog.Instance;
        _inner = new BcdeditDestructiveBcdCommand(_log, _storePath);
    }

    public int ExecuteCount { get; private set; }

    public string? LastCommandLine { get; private set; }

    public async Task<DestructiveCommandResult> ExecuteAsync(ResolvedBcdDeletionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!BcdIntegrationGuard.IsEnabled)
        {
            throw new InvalidOperationException("StoreBoundBcdCommand refused: CLEANSWITCH_BCD_TESTS is not enabled.");
        }

        IsolatedBcdStoreSession.CanonicalizeTempStorePath(_storePath);
        IsolatedBcdStoreSession.RefuseUnsafeBcdedit(["/store", _storePath, "/delete", target.FormattedId]);

        _log.Info(
            "bcd-isolated",
            "About to run isolated bcdedit /delete. " +
            $"store={_storePath} boot1={target.FormattedId}");

        var result = await _inner.ExecuteAsync(target);
        ExecuteCount++;
        LastCommandLine = result.CommandLine;

        if (LooksLikeBareDelete(result.CommandLine) ||
            result.CommandLine.Contains("/store", StringComparison.OrdinalIgnoreCase) is false ||
            !result.CommandLine.Contains(_storePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "bcdedit command line was not bound to the temporary /store path. " +
                "Refusing to treat this as isolated. command=" + result.CommandLine);
        }

        return result;
    }

    internal static bool LooksLikeBareDelete(string commandLine)
    {
        var trimmed = commandLine.Trim();
        return Regex.IsMatch(
            trimmed,
            @"bcdedit(\.exe)?\s+/delete\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
