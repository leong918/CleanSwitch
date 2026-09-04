using System.Security.Cryptography;
using System.Security.Principal;
using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class WinReServicingWorkspaceTests
{
    private const string RecoveryGuid = "{fc583d45-a29c-11f1-b0e3-e548a1d3146f}";

    [Fact]
    public void Production_workspace_is_machine_level_and_not_user_temp()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CleanSwitch",
            "WinRE");

        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(WindowsWinReWorkspaceFactory.MachineRoot),
            ignoreCase: true);
        Assert.False(Path.GetFullPath(WindowsWinReWorkspaceFactory.MachineRoot).StartsWith(
            Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
        var source = File.ReadAllText(FindRepoFile("CleanSwitch", "Recovery", "WindowsWinReLauncher.cs"));
        Assert.DoesNotContain("Path.GetTempPath()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Cleanup-Mountpoints", source, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("reparse")]
    [InlineData("compressed")]
    [InlineData("encrypted")]
    public void Unsafe_workspace_attributes_fail_closed(string unsafeAttribute)
    {
        var facts = ValidFacts() with
        {
            HasReparsePoint = unsafeAttribute == "reparse",
            IsCompressed = unsafeAttribute == "compressed",
            IsEncrypted = unsafeAttribute == "encrypted"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WinReWorkspaceValidator.Validate(facts, WinReWorkspaceValidator.AbsoluteMinimumFreeBytes));

        Assert.Contains(unsafeAttribute, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_ntfs_or_non_fixed_workspace_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => WinReWorkspaceValidator.Validate(
            ValidFacts() with { IsLocalAbsolutePath = false }, WinReWorkspaceValidator.AbsoluteMinimumFreeBytes));
        Assert.Throws<InvalidOperationException>(() => WinReWorkspaceValidator.Validate(
            ValidFacts() with { FileSystem = "ReFS" }, WinReWorkspaceValidator.AbsoluteMinimumFreeBytes));
        Assert.Throws<InvalidOperationException>(() => WinReWorkspaceValidator.Validate(
            ValidFacts() with { IsFixedDrive = false }, WinReWorkspaceValidator.AbsoluteMinimumFreeBytes));
    }

    [Fact]
    public void Insufficient_workspace_space_fails_closed()
    {
        var required = WinReWorkspaceValidator.RequiredFreeBytes(800_000_000);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WinReWorkspaceValidator.Validate(ValidFacts() with { AvailableFreeBytes = required - 1 }, required));

        Assert.Contains("free bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nonempty_mount_or_missing_machine_acl_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => WinReWorkspaceValidator.Validate(
            ValidFacts() with { MountDirectoryEmpty = false }, WinReWorkspaceValidator.AbsoluteMinimumFreeBytes));
        Assert.Throws<InvalidOperationException>(() => WinReWorkspaceValidator.Validate(
            ValidFacts() with { SystemHasFullControl = false }, WinReWorkspaceValidator.AbsoluteMinimumFreeBytes));
        Assert.Throws<InvalidOperationException>(() => WinReWorkspaceValidator.Validate(
            ValidFacts() with { AdministratorsHaveFullControl = false }, WinReWorkspaceValidator.AbsoluteMinimumFreeBytes));
    }

    [Fact]
    public async Task Provisioning_services_only_byte_exact_copy_then_remounts_and_verifies()
    {
        using var fixture = PipelineFixture.Create(includeFallback: true);
        var sourceBefore = Hash(fixture.SourceWim);

        var result = await fixture.Provisioner.ProvisionAsync(fixture.Recovery, sourceBefore);

        Assert.True(result.Passed, result.Report.Describe());
        Assert.NotNull(result.PreparedImagePath);
        Assert.True(File.Exists(result.PreparedImagePath));
        Assert.NotNull(result.PreparedBundlePath);
        Assert.True(File.Exists(result.PreparedBundlePath));
        Assert.Equal(sourceBefore, Hash(fixture.SourceWim));
        Assert.Equal(2, fixture.Dism.MountCalls.Count);
        Assert.All(fixture.Dism.MountCalls, call =>
            Assert.Equal(result.PreparedImagePath, call.ImagePath, ignoreCase: true));
        Assert.False(fixture.Dism.MountCalls[0].ReadOnly);
        Assert.True(fixture.Dism.MountCalls[1].ReadOnly);
        Assert.Contains(result.Report.Checks, check => check.Name == "source-copy-hash" && check.Passed);
        Assert.Contains(result.Report.Checks, check => check.Name == "live-winre-unchanged" && check.Passed);
        Assert.Contains(result.Report.Checks, check => check.Name == "explicit-deployment-required" && check.Passed);
        Assert.Equal("source-copy", Path.GetFileName(fixture.Factory.LastWorkspace!.SourceCopyDirectory));
        Assert.Equal("mount", Path.GetFileName(fixture.Factory.LastWorkspace.MountDirectory));
        Assert.Equal("scratch", Path.GetFileName(fixture.Factory.LastWorkspace.ScratchDirectory));
        Assert.Equal("logs", Path.GetFileName(fixture.Factory.LastWorkspace.LogDirectory));
        Assert.Contains(fixture.Dism.AllArguments.SelectMany(arguments => arguments),
            argument => argument.StartsWith("/ScratchDir:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fixture.Dism.AllArguments.SelectMany(arguments => arguments),
            argument => argument.StartsWith("/LogPath:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Copied_wim_hash_mismatch_fails_before_any_dism_operation()
    {
        using var fixture = PipelineFixture.Create(includeFallback: true, copier: new CorruptingCopier());
        var sourceBefore = Hash(fixture.SourceWim);

        var result = await fixture.Provisioner.ProvisionAsync(fixture.Recovery, sourceBefore);

        Assert.False(result.Passed);
        Assert.Contains(result.Report.Checks, check => check.Name == "source-copy-hash" && !check.Passed);
        Assert.Empty(fixture.Dism.AllArguments);
        Assert.Equal(sourceBefore, Hash(fixture.SourceWim));
        Assert.False(Directory.Exists(fixture.Factory.LastWorkspace!.OperationRoot));
    }

    [Fact]
    public async Task Missing_stock_recenv_fails_without_touching_live_wim_bcd_disk_or_state()
    {
        using var fixture = PipelineFixture.Create(includeFallback: false);
        var sourceBefore = Hash(fixture.SourceWim);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provisioner.ProvisionAsync(fixture.Recovery, sourceBefore));

        Assert.Contains("approved fallback", exception.Message, StringComparison.Ordinal);
        Assert.Equal(sourceBefore, Hash(fixture.SourceWim));
        Assert.Single(fixture.Dism.MountCalls);
        Assert.DoesNotContain(fixture.Dism.AllArguments.SelectMany(arguments => arguments), argument =>
            argument.Contains("bcdedit", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("diskpart", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("retirement-state", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Review_mount_failure_is_fail_closed_and_source_is_unchanged()
    {
        using var fixture = PipelineFixture.Create(includeFallback: true, failMount: true);
        var sourceBefore = Hash(fixture.SourceWim);

        var result = await fixture.Validator.ValidateAsync(fixture.Recovery);

        Assert.False(result.Passed);
        Assert.Contains(result.Report.Checks,
            check => check.Name == "recovery-image-inspectable" && !check.Passed);
        Assert.Equal(sourceBefore, Hash(fixture.SourceWim));
        Assert.DoesNotContain(fixture.Dism.AllArguments.SelectMany(arguments => arguments), argument =>
            argument.Contains("bcdedit", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("diskpart", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("retirement-state", StringComparison.OrdinalIgnoreCase));
    }

    [WinReWimIntegrationFact]
    public async Task Disposable_wim_is_copied_serviced_committed_and_remounted_without_live_mutation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        Assert.True(principal.IsInRole(WindowsBuiltInRole.Administrator),
            "Disposable DISM WIM integration must run elevated.");

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CleanSwitch",
            "WinRE-IntegrationTests",
            "case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var captureRoot = Path.Combine(root, "capture-root");
            var fallback = Path.Combine(captureRoot, WinReLauncherContract.FallbackExecutableRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
            File.WriteAllText(fallback, "disposable stock RecEnv fixture");
            var system32 = Path.Combine(captureRoot, "Windows", "System32");
            Directory.CreateDirectory(system32);
            File.WriteAllText(Path.Combine(system32, "winpeshl.ini"),
                "[LaunchApp]\r\nAppPath=X:\\sources\\recovery\\recenv.exe\r\n");

            var sourceWim = Path.Combine(root, "live-fixture.wim");
            var capture = await LoggedProcess.RunAsync(
                "dism.exe",
                [
                    "/English",
                    "/Capture-Image",
                    $"/ImageFile:{sourceWim}",
                    $"/CaptureDir:{captureRoot}",
                    "/Name:Disposable CleanSwitch WinRE fixture",
                    "/Compress:fast",
                    "/CheckIntegrity"
                ],
                NullOperationLog.Instance);
            Assert.True(capture.ExitCode == 0, LoggedProcess.Describe(capture));
            var sourceBefore = Hash(sourceWim);

            var drive = Path.GetPathRoot(sourceWim)!.TrimEnd(Path.DirectorySeparatorChar);
            var suffix = sourceWim[Path.GetPathRoot(sourceWim)!.Length..].Replace('/', '\\');
            var device = $"ramdisk=[{drive}]\\{suffix},{{11111111-1111-1111-1111-111111111111}}";
            var entry = new BcdEntry(
                RecoveryGuid, "Windows Recovery Environment", @"\windows\system32\winload.efi",
                device, device, string.Empty, string.Empty, "Windows Boot Loader");
            var resolutionReport = new ValidationReport("fixture recovery");
            resolutionReport.Pass("fixture", "resolved");
            var recovery = new RecoveryEntryResolution(RecoveryGuid, entry, resolutionReport);

            var factory = new TestWorkspaceFactory(Path.Combine(root, "machine"));
            var options = RetirementFixtures.Options(enableDestructive: true);
            options.RecoveryDataVolumeGptId = "{47c8a288-ae3d-4aca-b1ab-d4deceae9d02}";
            options.RecoveryDataFolderName = "CleanSwitchData";
            var provisioner = new WindowsWinReLauncherProvisioner(
                options,
                NullOperationLog.Instance,
                factory,
                new LoggedWinReDismRunner(),
                new WinReFileCopier(),
                Environment.ProcessPath);

            var result = await provisioner.ProvisionAsync(recovery, sourceBefore);

            Assert.True(result.Passed, result.Report.Describe());
            Assert.Equal(sourceBefore, Hash(sourceWim));
            Assert.NotNull(result.PreparedImagePath);
            Assert.NotNull(result.PreparedBundlePath);
            Assert.True(File.Exists(result.PreparedBundlePath));
            Assert.False(string.Equals(
                Path.GetFullPath(sourceWim),
                Path.GetFullPath(result.PreparedImagePath!),
                StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Report.Checks, check => check.Name == "live-winre-unchanged" && check.Passed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scoped_cleanup_refuses_paths_not_owned_by_operation_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanSwitch-Workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new WinReServicingWorkspace(root, Path.Combine(root, "not-owned"));
            Directory.CreateDirectory(workspace.OperationRoot);

            Assert.False(workspace.TryCleanupOwned(out var diagnostic));
            Assert.Contains("Refused", diagnostic, StringComparison.Ordinal);
            Assert.True(Directory.Exists(workspace.OperationRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Scoped_cleanup_reports_locked_residue_and_never_escalates_to_global_dism_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanSwitch-Workspace-" + Guid.NewGuid().ToString("N"));
        var operation = Path.Combine(root, "operation-test");
        var workspace = new WinReServicingWorkspace(root, operation);
        Directory.CreateDirectory(workspace.LogDirectory);
        var lockedPath = Path.Combine(workspace.OperationRoot, "locked.bin");
        File.WriteAllText(lockedPath, "locked residue fixture");
        var dism = new NoMountedImagesDismRunner();
        var report = new ValidationReport("cleanup fixture");

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await WinReWorkspaceCleanup.AddResultAsync(
                workspace, dism, NullOperationLog.Instance, report);

            Assert.False(report.Passed);
            Assert.Contains(report.Checks, check =>
                check.Name == "workspace-cleanup" &&
                !check.Passed &&
                check.Detail.Contains("no mounted images", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(workspace.OperationRoot));
            Assert.Single(dism.AllArguments);
            Assert.Contains("/Get-MountedImageInfo", dism.AllArguments[0], StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(dism.AllArguments.SelectMany(arguments => arguments), argument =>
                argument.Contains("Cleanup-Mountpoints", StringComparison.OrdinalIgnoreCase));
        }

        Directory.Delete(root, recursive: true);
    }

    private static WinReWorkspaceValidationFacts ValidFacts() => new(
        IsLocalAbsolutePath: true,
        IsFixedDrive: true,
        FileSystem: "NTFS",
        HasReparsePoint: false,
        IsCompressed: false,
        IsEncrypted: false,
        MountDirectoryEmpty: true,
        AvailableFreeBytes: long.MaxValue,
        SystemHasFullControl: true,
        AdministratorsHaveFullControl: true);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(parts[^1]);
    }

    private sealed class CorruptingCopier : IWinReFileCopier
    {
        public void Copy(string source, string destination)
        {
            File.Copy(source, destination);
            File.AppendAllText(destination, "corrupt");
        }
    }

    private sealed class NoMountedImagesDismRunner : IWinReDismRunner
    {
        public List<IReadOnlyList<string>> AllArguments { get; } = [];

        public Task<LoggedProcessResult> RunAsync(IReadOnlyList<string> arguments, IOperationLog log)
        {
            AllArguments.Add(arguments.ToArray());
            return Task.FromResult(new LoggedProcessResult(
                0,
                "No mounted images found.",
                string.Empty,
                "dism.exe " + string.Join(' ', arguments)));
        }
    }

    private sealed class TestWorkspaceFactory : IWinReWorkspaceFactory
    {
        private readonly string _machineRoot;

        public TestWorkspaceFactory(string machineRoot) => _machineRoot = machineRoot;

        public WinReServicingWorkspace? LastWorkspace { get; private set; }

        public WinReServicingWorkspace Create(long sourceWimBytes)
        {
            var operation = Path.Combine(_machineRoot, "operation-" + Guid.NewGuid().ToString("N"));
            var workspace = new WinReServicingWorkspace(_machineRoot, operation);
            Directory.CreateDirectory(workspace.SourceCopyDirectory);
            Directory.CreateDirectory(workspace.MountDirectory);
            Directory.CreateDirectory(workspace.ScratchDirectory);
            Directory.CreateDirectory(workspace.LogDirectory);
            LastWorkspace = workspace;
            return workspace;
        }
    }

    private sealed class FakeDismRunner : IWinReDismRunner
    {
        private readonly string _snapshot;
        private readonly bool _includeFallback;
        private readonly bool _failMount;

        public FakeDismRunner(string snapshot, bool includeFallback, bool failMount)
        {
            _snapshot = snapshot;
            _includeFallback = includeFallback;
            _failMount = failMount;
        }

        public List<(string ImagePath, bool ReadOnly)> MountCalls { get; } = [];
        public List<IReadOnlyList<string>> AllArguments { get; } = [];

        public Task<LoggedProcessResult> RunAsync(IReadOnlyList<string> arguments, IOperationLog log)
        {
            AllArguments.Add(arguments.ToArray());
            var mountArgument = arguments.FirstOrDefault(argument =>
                argument.StartsWith("/MountDir:", StringComparison.OrdinalIgnoreCase));
            var mount = mountArgument?["/MountDir:".Length..] ?? string.Empty;

            if (arguments.Contains("/Mount-Image", StringComparer.OrdinalIgnoreCase))
            {
                var image = arguments.First(argument =>
                    argument.StartsWith("/ImageFile:", StringComparison.OrdinalIgnoreCase))["/ImageFile:".Length..];
                var readOnly = arguments.Contains("/ReadOnly", StringComparer.OrdinalIgnoreCase);
                MountCalls.Add((image, readOnly));
                if (_failMount)
                {
                    return Task.FromResult(new LoggedProcessResult(
                        1920, string.Empty, "The file cannot be accessed by the system.",
                        "dism.exe " + string.Join(' ', arguments)));
                }
                RestoreSnapshot(mount);
            }
            else if (arguments.Contains("/Unmount-Image", StringComparer.OrdinalIgnoreCase))
            {
                if (arguments.Contains("/Commit", StringComparer.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(_snapshot)) Directory.Delete(_snapshot, recursive: true);
                    CopyDirectory(mount, _snapshot);
                }

                ClearDirectory(mount);
            }

            return Task.FromResult(new LoggedProcessResult(0, "fake DISM success", string.Empty,
                "dism.exe " + string.Join(' ', arguments)));
        }

        private void RestoreSnapshot(string mount)
        {
            ClearDirectory(mount);
            if (Directory.Exists(_snapshot))
            {
                CopyDirectory(_snapshot, mount);
                return;
            }

            Directory.CreateDirectory(Path.Combine(mount, "Windows", "System32"));
            File.WriteAllText(Path.Combine(mount, "Windows", "System32", "winpeshl.ini"),
                "[LaunchApp]\r\nAppPath=X:\\sources\\recovery\\recenv.exe\r\n");
            if (_includeFallback)
            {
                var fallback = Path.Combine(mount, WinReLauncherContract.FallbackExecutableRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
                File.WriteAllText(fallback, "stock recenv");
            }
        }

        private static void ClearDirectory(string path)
        {
            Directory.CreateDirectory(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }

    private sealed class PipelineFixture : IDisposable
    {
        private PipelineFixture(
            string root,
            string sourceWim,
            RecoveryEntryResolution recovery,
            TestWorkspaceFactory factory,
            FakeDismRunner dism,
            WindowsWinReLauncherProvisioner provisioner,
            WindowsWinReLauncherValidator validator)
        {
            Root = root;
            SourceWim = sourceWim;
            Recovery = recovery;
            Factory = factory;
            Dism = dism;
            Provisioner = provisioner;
            Validator = validator;
        }

        public string Root { get; }
        public string SourceWim { get; }
        public RecoveryEntryResolution Recovery { get; }
        public TestWorkspaceFactory Factory { get; }
        public FakeDismRunner Dism { get; }
        public WindowsWinReLauncherProvisioner Provisioner { get; }
        public WindowsWinReLauncherValidator Validator { get; }

        public static PipelineFixture Create(
            bool includeFallback,
            IWinReFileCopier? copier = null,
            bool failMount = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "CleanSwitch-WinRE-Pipeline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourceWim = Path.Combine(root, "live-source.wim");
            File.WriteAllText(sourceWim, "unchanged live WinRE fixture");
            var drive = Path.GetPathRoot(sourceWim)!.TrimEnd(Path.DirectorySeparatorChar);
            var suffix = sourceWim[Path.GetPathRoot(sourceWim)!.Length..].Replace('/', '\\');
            var device = $"ramdisk=[{drive}]\\{suffix},{{11111111-1111-1111-1111-111111111111}}";
            var entry = new BcdEntry(
                RecoveryGuid, "Windows Recovery Environment", @"\windows\system32\winload.efi",
                device, device, string.Empty, string.Empty, "Windows Boot Loader");
            var resolutionReport = new ValidationReport("fixture recovery");
            resolutionReport.Pass("fixture", "resolved");
            var recovery = new RecoveryEntryResolution(RecoveryGuid, entry, resolutionReport);

            var machineRoot = Path.Combine(root, "machine");
            var factory = new TestWorkspaceFactory(machineRoot);
            var dism = new FakeDismRunner(Path.Combine(root, "fake-wim-snapshot"), includeFallback, failMount);
            var options = RetirementFixtures.Options(enableDestructive: true);
            options.RecoveryDataVolumeGptId = "{47c8a288-ae3d-4aca-b1ab-d4deceae9d02}";
            options.RecoveryDataFolderName = "CleanSwitchData";
            var provisioner = new WindowsWinReLauncherProvisioner(
                options, NullOperationLog.Instance, factory, dism, copier ?? new WinReFileCopier(),
                Environment.ProcessPath);
            var validator = new WindowsWinReLauncherValidator(
                options, NullOperationLog.Instance, factory, dism, new WinReFileCopier(),
                Environment.ProcessPath);
            return new PipelineFixture(root, sourceWim, recovery, factory, dism, provisioner, validator);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
