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

    public WindowsWinReLauncherValidator(CleanSwitchOptions options, IOperationLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
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
            expected = CreateCurrentExpectation(_options, recovery.Identifier);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            report.Fail("approved-payload-readable", exception.Message);
            return new WinReLauncherValidationResult(report, imagePath);
        }

        try
        {
            await using var mount = await DismWinReImageMount.MountAsync(imagePath, readOnly: true, _log, cancellationToken);
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

        foreach (var check in report.Checks)
        {
            _log.Write(check.Passed ? OperationLogLevel.Info : OperationLogLevel.Warning,
                "winre-launcher", check.ToString());
        }

        return new WinReLauncherValidationResult(report, imagePath);
    }

    internal static WinReLauncherExpectation CreateCurrentExpectation(CleanSwitchOptions options, string recoveryGuid)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("The current CleanSwitch executable path is unavailable.");
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

    public WindowsWinReLauncherProvisioner(CleanSwitchOptions options, IOperationLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<WinReLauncherValidationResult> ProvisionAsync(
        RecoveryEntryResolution recovery,
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

        var expected = WindowsWinReLauncherValidator.CreateCurrentExpectation(_options, recovery.Identifier);
        await using (var mount = await DismWinReImageMount.MountAsync(imagePath, readOnly: false, _log, cancellationToken))
        {
            WinReLauncherContract.WritePayload(mount.MountDirectory, expected);
            var staged = WinReLauncherContract.ValidateOfflineRoot(mount.MountDirectory, expected);
            if (!staged.Passed)
            {
                return new WinReLauncherValidationResult(staged, imagePath);
            }

            await mount.CommitAsync(cancellationToken);
        }

        var validator = new WindowsWinReLauncherValidator(_options, _log);
        return await validator.ValidateAsync(recovery, cancellationToken);
    }
}

internal sealed class DismWinReImageMount : IAsyncDisposable
{
    private readonly IOperationLog _log;
    private bool _unmounted;

    private DismWinReImageMount(string mountDirectory, IOperationLog log)
    {
        MountDirectory = mountDirectory;
        _log = log;
    }

    public string MountDirectory { get; }

    public static async Task<DismWinReImageMount> MountAsync(
        string imagePath,
        bool readOnly,
        IOperationLog log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mountDirectory = Path.Combine(Path.GetTempPath(), "CleanSwitch-WinRE-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mountDirectory);
        var arguments = new List<string>
        {
            "/English",
            "/Mount-Image",
            $"/ImageFile:{imagePath}",
            "/Index:1",
            $"/MountDir:{mountDirectory}"
        };
        if (readOnly)
        {
            arguments.Add("/ReadOnly");
        }

        var result = await LoggedProcess.RunAsync("dism.exe", arguments, log);
        if (result.ExitCode != 0)
        {
            TryDeleteUniqueMountDirectory(mountDirectory);
            throw new RetirementExecutionException(
                "DISM could not mount the selected WinRE image. " + LoggedProcess.Describe(result));
        }

        return new DismWinReImageMount(mountDirectory, log);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_unmounted)
        {
            throw new InvalidOperationException("The WinRE image is already unmounted.");
        }

        var result = await LoggedProcess.RunAsync(
            "dism.exe", ["/English", "/Unmount-Image", $"/MountDir:{MountDirectory}", "/Commit"], _log);
        if (result.ExitCode != 0)
        {
            throw new RetirementExecutionException(
                "DISM could not commit the WinRE launcher payload. " + LoggedProcess.Describe(result));
        }

        _unmounted = true;
        TryDeleteUniqueMountDirectory(MountDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_unmounted)
        {
            var result = await LoggedProcess.RunAsync(
                "dism.exe", ["/English", "/Unmount-Image", $"/MountDir:{MountDirectory}", "/Discard"], _log);
            _unmounted = result.ExitCode == 0;
            if (!_unmounted)
            {
                throw new RetirementExecutionException(
                    "DISM could not unmount the temporary WinRE inspection mount. " +
                    LoggedProcess.Describe(result));
            }
        }

        if (_unmounted)
        {
            TryDeleteUniqueMountDirectory(MountDirectory);
        }
    }

    private static void TryDeleteUniqueMountDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("CleanSwitch-WinRE-", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
