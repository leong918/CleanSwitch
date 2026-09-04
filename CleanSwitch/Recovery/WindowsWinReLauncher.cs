using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>Resolves the exact winre.wim named by a validated recovery BCD object.</summary>
public static class WinReImagePathResolver
{
    public static bool TryResolve(BcdEntry? entry, out string imagePath, out string diagnostic)
    {
        imagePath = string.Empty;
        diagnostic = string.Empty;

        if (entry is null)
        {
            diagnostic = "The validated RecoveryGuid did not include its BCD entry.";
            return false;
        }

        var candidates = new[] { entry.Device, entry.OsDevice }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (TryResolveRamdisk(candidate, out var path))
            {
                resolved.Add(path);
            }
        }

        if (resolved.Count != 1)
        {
            diagnostic = resolved.Count == 0
                ? $"Recovery entry {entry.Identifier} has no resolvable ramdisk winre.wim device."
                : $"Recovery entry {entry.Identifier} ambiguously resolves to {resolved.Count} different WIM paths.";
            return false;
        }

        imagePath = resolved.Single();
        if (!File.Exists(imagePath))
        {
            diagnostic = $"Recovery entry {entry.Identifier} resolves to '{imagePath}', but that WIM is unavailable.";
            imagePath = string.Empty;
            return false;
        }

        diagnostic = $"Recovery entry {entry.Identifier} resolves to exact image '{imagePath}'.";
        return true;
    }

    internal static bool TryResolveRamdisk(string text, out string imagePath)
    {
        imagePath = string.Empty;
        var ramdisk = text.IndexOf("ramdisk=[", StringComparison.OrdinalIgnoreCase);
        if (ramdisk < 0)
        {
            return false;
        }

        var deviceStart = ramdisk + "ramdisk=[".Length;
        var close = text.IndexOf(']', deviceStart);
        if (close <= deviceStart)
        {
            return false;
        }

        var device = text[deviceStart..close].Trim();
        var suffix = text[(close + 1)..];
        var comma = suffix.IndexOf(',');
        if (comma >= 0)
        {
            suffix = suffix[..comma];
        }

        suffix = suffix.Trim().Replace('/', '\\');
        if (!suffix.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string root;
        if (device.Length == 2 && char.IsLetter(device[0]) && device[1] == ':')
        {
            root = char.ToUpperInvariant(device[0]) + @":\";
        }
        else if (device.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase) &&
                 device["\\Device\\HarddiskVolume".Length..].All(char.IsDigit))
        {
            root = @"\\?\GLOBALROOT" + device.TrimEnd('\\') + @"\";
        }
        else
        {
            return false;
        }

        imagePath = Path.GetFullPath(Path.Combine(root, suffix.TrimStart('\\')));
        return true;
    }
}

public sealed class WindowsWinReLauncherValidator : IWinReLauncherValidator
{
    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;
    private readonly IWinReWorkspaceFactory _workspaceFactory;
    private readonly IWinReDismRunner _dism;
    private readonly IWinReFileCopier _copier;
    private readonly string? _sourceExecutablePath;

    public WindowsWinReLauncherValidator(CleanSwitchOptions options, IOperationLog? log = null)
        : this(options, log, new WindowsWinReWorkspaceFactory(), new LoggedWinReDismRunner(), new WinReFileCopier())
    {
    }

    internal WindowsWinReLauncherValidator(
        CleanSwitchOptions options,
        IOperationLog? log,
        IWinReWorkspaceFactory workspaceFactory,
        IWinReDismRunner dism,
        IWinReFileCopier copier,
        string? sourceExecutablePath = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
        _workspaceFactory = workspaceFactory ?? throw new ArgumentNullException(nameof(workspaceFactory));
        _dism = dism ?? throw new ArgumentNullException(nameof(dism));
        _copier = copier ?? throw new ArgumentNullException(nameof(copier));
        _sourceExecutablePath = sourceExecutablePath;
    }

    public async Task<WinReLauncherValidationResult> ValidateAsync(
        RecoveryEntryResolution recovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        var report = new ValidationReport("WinRE CleanSwitch launcher provisioning");

        if (recovery.Identifier is null || recovery.Entry is null)
        {
            report.Fail("recovery-entry-bound", "A validated concrete RecoveryGuid and BCD entry are required.");
            return new WinReLauncherValidationResult(report);
        }

        if (!WinReImagePathResolver.TryResolve(recovery.Entry, out var imagePath, out var pathDiagnostic))
        {
            report.Fail("recovery-image-resolved", pathDiagnostic);
            return new WinReLauncherValidationResult(report);
        }

        report.Pass("recovery-image-resolved", pathDiagnostic);

        WinReLauncherExpectation expected;
        try
        {
            expected = CreateCurrentExpectation(_options, recovery.Identifier, _sourceExecutablePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            report.Fail("approved-payload-readable", exception.Message);
            return new WinReLauncherValidationResult(report, imagePath);
        }

        WinReServicingWorkspace? workspace = null;
        try
        {
            var sourceSize = new FileInfo(imagePath).Length;
            workspace = _workspaceFactory.Create(sourceSize);
            if (!WinReImageCopy.TryCopyAndVerify(imagePath, workspace.PreparedImagePath, _copier, report))
            {
                return new WinReLauncherValidationResult(report, imagePath);
            }

            await using var mount = await DismWinReImageMount.MountAsync(
                workspace.PreparedImagePath, readOnly: true, workspace, _dism, _log, cancellationToken);
            var payload = WinReLauncherContract.ValidateOfflineRoot(mount.MountDirectory, expected);
            foreach (var check in payload.Checks)
            {
                report.Add(check.Name, check.Passed, check.Detail);
            }
        }
        catch (Exception exception) when (
            exception is RetirementExecutionException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            report.Fail("recovery-image-inspectable", $"The selected WinRE image could not be inspected fail-closed: {exception.Message}");
        }
        finally
        {
            if (workspace is not null)
            {
                await WinReWorkspaceCleanup.AddResultAsync(workspace, _dism, _log, report);
            }
        }

        foreach (var check in report.Checks)
        {
            _log.Write(check.Passed ? OperationLogLevel.Info : OperationLogLevel.Warning,
                "winre-launcher", check.ToString());
        }

        return new WinReLauncherValidationResult(report, imagePath);
    }

    internal static WinReLauncherExpectation CreateCurrentExpectation(
        CleanSwitchOptions options,
        string recoveryGuid,
        string? sourceExecutablePath = null)
    {
        var executable = sourceExecutablePath ??
            Path.Combine(AppContext.BaseDirectory, WinReLauncherContract.RecoveryExecutableFileName);
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                $"The dedicated WinRE recovery executable is unavailable: '{executable}'.");
        }

        return WinReLauncherContract.CreateExpectation(
            options,
            recoveryGuid,
            executable,
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
    }
}

/// <summary>
/// Offline WIM provisioner. It never changes BCD, retirement state, or partitions. Callers
/// must separately prove there is no active operation before permitting a write mount.
/// </summary>
public sealed class WindowsWinReLauncherProvisioner
{
    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;
    private readonly IWinReWorkspaceFactory _workspaceFactory;
    private readonly IWinReDismRunner _dism;
    private readonly IWinReFileCopier _copier;
    private readonly string? _sourceExecutablePath;

    public WindowsWinReLauncherProvisioner(CleanSwitchOptions options, IOperationLog? log = null)
        : this(options, log, new WindowsWinReWorkspaceFactory(), new LoggedWinReDismRunner(), new WinReFileCopier())
    {
    }

    internal WindowsWinReLauncherProvisioner(
        CleanSwitchOptions options,
        IOperationLog? log,
        IWinReWorkspaceFactory workspaceFactory,
        IWinReDismRunner dism,
        IWinReFileCopier copier,
        string? sourceExecutablePath = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
        _workspaceFactory = workspaceFactory ?? throw new ArgumentNullException(nameof(workspaceFactory));
        _dism = dism ?? throw new ArgumentNullException(nameof(dism));
        _copier = copier ?? throw new ArgumentNullException(nameof(copier));
        _sourceExecutablePath = sourceExecutablePath;
    }

    public Task<WinReLauncherValidationResult> ProvisionAsync(
        RecoveryEntryResolution recovery,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Expected original Winre.wim SHA256 is required as an explicit reviewed provisioning input.");

    public async Task<WinReLauncherValidationResult> ProvisionAsync(
        RecoveryEntryResolution recovery,
        string expectedOriginalWimSha256,
        CancellationToken cancellationToken = default)
    {
        var diagnostic = "Recovery entry is incomplete.";
        if (recovery.Identifier is null || recovery.Entry is null ||
            !WinReImagePathResolver.TryResolve(recovery.Entry, out var imagePath, out diagnostic))
        {
            var rejected = new ValidationReport("WinRE CleanSwitch launcher provisioning");
            rejected.Fail("recovery-image-resolved", diagnostic);
            return new WinReLauncherValidationResult(rejected);
        }

        var expectedHash = WinReDeploymentHashPolicy.RequireSha256(
            expectedOriginalWimSha256,
            "Expected original Winre.wim SHA256");
        var observedHash = HashFile(imagePath);
        WinReDeploymentHashPolicy.RequireExpectedMatchesObserved(
            expectedHash,
            observedHash,
            "WinRE launcher preparation preflight");

        var expected = WindowsWinReLauncherValidator.CreateCurrentExpectation(
            _options, recovery.Identifier, _sourceExecutablePath);
        var report = new ValidationReport("WinRE CleanSwitch launcher prepared image");
        var workspace = _workspaceFactory.Create(new FileInfo(imagePath).Length);
        var preservePreparedImage = false;
        try
        {
            report.Pass("recovery-image-resolved", diagnostic);
            if (!WinReImageCopy.TryCopyAndVerify(imagePath, workspace.PreparedImagePath, _copier, report))
            {
                return new WinReLauncherValidationResult(report, imagePath);
            }

            await using (var mount = await DismWinReImageMount.MountAsync(
                             workspace.PreparedImagePath, readOnly: false, workspace, _dism, _log, cancellationToken))
            {
                WinReLauncherContract.WritePayload(mount.MountDirectory, expected);
                var staged = WinReLauncherContract.ValidateOfflineRoot(mount.MountDirectory, expected);
                foreach (var check in staged.Checks)
                {
                    report.Add("staged-" + check.Name, check.Passed, check.Detail);
                }

                if (!staged.Passed)
                {
                    return new WinReLauncherValidationResult(report, imagePath);
                }

                await mount.CommitAsync(cancellationToken);
            }

            await using (var verificationMount = await DismWinReImageMount.MountAsync(
                             workspace.PreparedImagePath, readOnly: true, workspace, _dism, _log, cancellationToken))
            {
                var verified = WinReLauncherContract.ValidateOfflineRoot(verificationMount.MountDirectory, expected);
                foreach (var check in verified.Checks)
                {
                    report.Add("verified-" + check.Name, check.Passed, check.Detail);
                }

                if (!verified.Passed)
                {
                    return new WinReLauncherValidationResult(report, imagePath);
                }
            }

            preservePreparedImage = true;
            var preparedBundlePath = Path.Combine(workspace.OperationRoot, "prepared-winre-bundle.json");
            var bundle = new PreparedWinReBundleManifest
            {
                BundleId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                PreparedWimFileName = Path.GetFileName(workspace.PreparedImagePath),
                PreparedWimSize = new FileInfo(workspace.PreparedImagePath).Length,
                PreparedWimSha256 = HashFile(workspace.PreparedImagePath),
                OriginalLiveWimPath = Path.GetFullPath(imagePath),
                OriginalLiveWimSize = new FileInfo(imagePath).Length,
                OriginalLiveWimSha256 = observedHash,
                ExpectedOriginalWimSha256 = expectedHash,
                ObservedOriginalWimSha256 = observedHash,
                Launcher = expected.Manifest
            };
            WriteBundleDurably(preparedBundlePath, bundle);
            report.Pass(
                "live-winre-unchanged",
                $"Prepared and verified '{workspace.PreparedImagePath}'. The live WIM '{imagePath}' was read only and was not serviced.");
            report.Pass(
                "explicit-deployment-required",
                "Installing the prepared image into the registered WinRE location requires a separate, explicitly authorized deployment step.");
            return new WinReLauncherValidationResult(report, imagePath, workspace.PreparedImagePath, preparedBundlePath);
        }
        finally
        {
            if (!preservePreparedImage)
            {
                await WinReWorkspaceCleanup.AddResultAsync(workspace, _dism, _log, report);
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static void WriteBundleDurably(string path, PreparedWinReBundleManifest bundle)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(bundle, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }) + Environment.NewLine);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

internal interface IWinReFileCopier
{
    void Copy(string source, string destination);
}

internal sealed class WinReFileCopier : IWinReFileCopier
{
    public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: false);
}

internal static class WinReImageCopy
{
    public static bool TryCopyAndVerify(
        string source,
        string destination,
        IWinReFileCopier copier,
        ValidationReport report)
    {
        var sourceInfo = new FileInfo(source);
        var sourceHash = HashFile(source);
        report.Pass("source-wim-recorded", $"size={sourceInfo.Length}, sha256={sourceHash}, path='{source}'.");
        copier.Copy(source, destination);

        var copyInfo = new FileInfo(destination);
        var copyHash = HashFile(destination);
        var sizeMatches = sourceInfo.Length == copyInfo.Length;
        var hashMatches = string.Equals(sourceHash, copyHash, StringComparison.OrdinalIgnoreCase);
        report.Add("source-copy-size", sizeMatches,
            $"source={sourceInfo.Length}, copy={copyInfo.Length}, path='{destination}'.");
        report.Add("source-copy-hash", hashMatches,
            $"source={sourceHash}, copy={copyHash}.");
        return sizeMatches && hashMatches;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}

internal interface IWinReDismRunner
{
    Task<LoggedProcessResult> RunAsync(IReadOnlyList<string> arguments, IOperationLog log);
}

internal sealed class LoggedWinReDismRunner : IWinReDismRunner
{
    public Task<LoggedProcessResult> RunAsync(IReadOnlyList<string> arguments, IOperationLog log) =>
        LoggedProcess.RunAsync("dism.exe", arguments, log);
}

internal static class WinReWorkspaceCleanup
{
    public static async Task AddResultAsync(
        WinReServicingWorkspace workspace,
        IWinReDismRunner dism,
        IOperationLog log,
        ValidationReport report)
    {
        var cleaned = workspace.TryCleanupOwned(out var diagnostic);
        if (cleaned)
        {
            report.Pass("workspace-cleanup", diagnostic);
            return;
        }

        var mounted = await dism.RunAsync(
            ["/English", "/Get-MountedImageInfo"], log);
        var noMountedImages = mounted.ExitCode == 0 &&
                              mounted.StdOut.Contains("No mounted images found", StringComparison.OrdinalIgnoreCase);
        var detail = diagnostic + " " +
                     (noMountedImages
                         ? "DISM reports no mounted images; residue was reported and no broader cleanup was attempted."
                         : "DISM did not prove an empty mounted-image list; residue was reported and no broader cleanup was attempted.");
        report.Fail("workspace-cleanup", detail);
        log.Write(OperationLogLevel.Warning, "winre-workspace", detail);
    }
}

internal sealed class DismWinReImageMount : IAsyncDisposable
{
    private readonly IOperationLog _log;
    private readonly IWinReDismRunner _dism;
    private readonly WinReServicingWorkspace _workspace;
    private bool _unmounted;

    private DismWinReImageMount(
        WinReServicingWorkspace workspace,
        IWinReDismRunner dism,
        IOperationLog log)
    {
        _workspace = workspace;
        _dism = dism;
        MountDirectory = workspace.MountDirectory;
        _log = log;
    }

    public string MountDirectory { get; }

    public static async Task<DismWinReImageMount> MountAsync(
        string imagePath,
        bool readOnly,
        WinReServicingWorkspace workspace,
        IWinReDismRunner dism,
        IOperationLog log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workspace.RequireEmptyMountDirectory();
        var arguments = new List<string>
        {
            "/English",
            $"/ScratchDir:{workspace.ScratchDirectory}",
            $"/LogPath:{workspace.CreateDismLogPath(readOnly ? "mount-readonly" : "mount-service")}",
            "/Mount-Image",
            $"/ImageFile:{imagePath}",
            "/Index:1",
            $"/MountDir:{workspace.MountDirectory}"
        };
        if (readOnly)
        {
            arguments.Add("/ReadOnly");
        }

        var result = await dism.RunAsync(arguments, log);
        if (result.ExitCode != 0)
        {
            throw new RetirementExecutionException(
                "DISM could not mount the prepared WinRE image. " + LoggedProcess.Describe(result) +
                Environment.NewLine + $"Scoped workspace residue, if any: '{workspace.OperationRoot}'. " +
                "No global DISM cleanup was attempted.");
        }

        return new DismWinReImageMount(workspace, dism, log);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_unmounted)
        {
            throw new InvalidOperationException("The WinRE image is already unmounted.");
        }

        var result = await _dism.RunAsync(
            [
                "/English",
                $"/ScratchDir:{_workspace.ScratchDirectory}",
                $"/LogPath:{_workspace.CreateDismLogPath("commit")}",
                "/Unmount-Image",
                $"/MountDir:{MountDirectory}",
                "/Commit"
            ], _log);
        if (result.ExitCode != 0)
        {
            throw new RetirementExecutionException(
                "DISM could not commit the WinRE launcher payload. " + LoggedProcess.Describe(result));
        }

        _unmounted = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_unmounted)
        {
            var result = await _dism.RunAsync(
                [
                    "/English",
                    $"/ScratchDir:{_workspace.ScratchDirectory}",
                    $"/LogPath:{_workspace.CreateDismLogPath("discard")}",
                    "/Unmount-Image",
                    $"/MountDir:{MountDirectory}",
                    "/Discard"
                ], _log);
            _unmounted = result.ExitCode == 0;
            if (!_unmounted)
            {
                throw new RetirementExecutionException(
                    "DISM could not unmount the temporary WinRE inspection mount. " +
                    LoggedProcess.Describe(result));
            }
        }

    }
}
