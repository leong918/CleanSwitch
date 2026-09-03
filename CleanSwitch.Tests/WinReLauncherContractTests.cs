using System.Text.Json;
using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class WinReLauncherContractTests
{
    private const string Recovery = "{fc583d45-a29c-11f1-b0e3-e548a1d3146f}";

    [Fact]
    public void Correct_customized_winre_passes_every_launcher_check()
    {
        using var fixture = LauncherFixture.Create();
        fixture.WriteValidPayload();

        var report = fixture.Validate();

        Assert.True(report.Passed, report.Describe());
        Assert.Contains(report.Checks, check => check.Name == "launcher-official-entrypoint" && check.Passed);
        Assert.Equal(
            ["--recovery-run", "--execute-deletion"],
            fixture.Expectation.Manifest.Arguments);
    }

    [Fact]
    public void Normal_winre_without_cleanswitch_customization_fails_closed()
    {
        using var fixture = LauncherFixture.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Windows", "System32"));
        File.WriteAllText(Path.Combine(fixture.Root, "Windows", "System32", "winpeshl.ini"),
            "[LaunchApp]\r\nAppPath=%SYSTEMROOT%\\System32\\recenv.exe\r\n");

        var report = fixture.Validate();

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "launcher-startup" && !check.Passed);
        Assert.Contains(report.Checks, check => check.Name == "launcher-manifest-present" && !check.Passed);
    }

    [Fact]
    public void Missing_launcher_fails_closed()
    {
        using var fixture = LauncherFixture.Create();

        var report = fixture.Validate();

        Assert.False(report.Passed);
        Assert.Contains("Missing", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_launcher_manifest_fails_closed()
    {
        using var fixture = LauncherFixture.Create();
        fixture.WriteValidPayload();
        fixture.RewriteManifest(manifest => manifest with { ProductVersion = "1.0.0+stale" });

        var report = fixture.Validate();

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "launcher-manifest-product-version" && !check.Passed);
    }

    [Fact]
    public void Wrong_embedded_binary_hash_fails_closed()
    {
        using var fixture = LauncherFixture.Create();
        fixture.WriteValidPayload();
        File.AppendAllText(Path.Combine(fixture.Root, WinReLauncherContract.ExecutableRelativePath), "tamper");

        var report = fixture.Validate();

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "launcher-executable-hash" && !check.Passed);
    }

    [Fact]
    public void Wrong_embedded_appsettings_hash_fails_closed()
    {
        using var fixture = LauncherFixture.Create();
        fixture.WriteValidPayload();
        File.AppendAllText(Path.Combine(fixture.Root, WinReLauncherContract.ConfigurationRelativePath), "tamper");

        var report = fixture.Validate();

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "launcher-configuration-hash" && !check.Passed);
    }

    [Fact]
    public void Wrong_recovery_guid_marker_fails_closed()
    {
        using var fixture = LauncherFixture.Create();
        fixture.WriteValidPayload();
        fixture.RewriteManifest(manifest => manifest with
        {
            RecoveryGuid = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}"
        });

        var report = fixture.Validate();

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "launcher-recovery-guid" && !check.Passed);
    }

    [Fact]
    public void Gui_or_default_startup_is_never_a_valid_recovery_continuation()
    {
        using var fixture = LauncherFixture.Create();
        fixture.WriteValidPayload();
        File.WriteAllText(
            Path.Combine(fixture.Root, WinReLauncherContract.WinpeshlRelativePath),
            "[LaunchApps]\r\n%SYSTEMDRIVE%\\CleanSwitchRecovery\\CleanSwitch.exe\r\n");
        fixture.RewriteManifest(manifest => manifest with { Arguments = [] });

        var report = fixture.Validate();

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "launcher-startup" && !check.Passed);
        Assert.Contains(report.Checks, check => check.Name == "launcher-official-entrypoint" && !check.Passed);
    }

    [Fact]
    public void Active_pending_state_forbids_winre_provisioning()
    {
        var state = new RetirementState { Status = RetirementStatus.Pending };
        var options = RetirementFixtures.Options(enableDestructive: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WinReLauncherProvisioningGuard.Validate(state, options, true, true));

        Assert.Contains("PENDING", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provisioning_requires_all_destructive_gates()
    {
        var options = RetirementFixtures.Options(enableDestructive: true);

        Assert.Throws<InvalidOperationException>(() =>
            WinReLauncherProvisioningGuard.Validate(null, options, false, true));
        Assert.Throws<InvalidOperationException>(() =>
            WinReLauncherProvisioningGuard.Validate(null, options, true, false));
        options.EnableDestructiveRetirement = false;
        Assert.Throws<InvalidOperationException>(() =>
            WinReLauncherProvisioningGuard.Validate(null, options, true, true));
    }

    [Theory]
    [InlineData(@"ramdisk=[R:]\Recovery\WindowsRE\winre.wim,{11111111-1111-1111-1111-111111111111}", @"R:\Recovery\WindowsRE\winre.wim")]
    [InlineData(@"ramdisk=[\Device\HarddiskVolume8]\Recovery\WindowsRE\winre.wim,{11111111-1111-1111-1111-111111111111}", @"\\?\GLOBALROOT\Device\HarddiskVolume8\Recovery\WindowsRE\winre.wim")]
    public void Recovery_wim_path_resolution_does_not_assume_installed_windows_drive_letter(
        string bcdDevice,
        string expected)
    {
        Assert.True(WinReImagePathResolver.TryResolveRamdisk(bcdDevice, out var actual));
        Assert.Equal(expected, actual, ignoreCase: true);
    }

    private sealed class LauncherFixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private LauncherFixture(string root, WinReLauncherExpectation expectation)
        {
            Root = root;
            Expectation = expectation;
        }

        public string Root { get; }
        public WinReLauncherExpectation Expectation { get; }

        public static LauncherFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "CleanSwitch-WinRE-Fixture-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var exe = Path.Combine(source, "CleanSwitch.exe");
            File.Copy(typeof(WinReLauncherContractTests).Assembly.Location, exe);
            var config = Path.Combine(source, "appsettings.json");
            File.WriteAllText(config, "{\"CleanSwitch\":{}}");
            var options = RetirementFixtures.Options(enableDestructive: true);
            options.RecoveryDataVolumeGptId = "{47c8a288-ae3d-4aca-b1ab-d4deceae9d02}";
            options.RecoveryDataFolderName = "CleanSwitchData";
            var expectation = WinReLauncherContract.CreateExpectation(options, Recovery, exe, config);
            return new LauncherFixture(root, expectation);
        }

        public void WriteValidPayload() => WinReLauncherContract.WritePayload(Root, Expectation);

        public ValidationReport Validate() => WinReLauncherContract.ValidateOfflineRoot(Root, Expectation);

        public void RewriteManifest(Func<WinReLauncherManifest, WinReLauncherManifest> change)
        {
            var path = Path.Combine(Root, WinReLauncherContract.ManifestRelativePath);
            var current = JsonSerializer.Deserialize<WinReLauncherManifest>(File.ReadAllText(path), JsonOptions)!;
            File.WriteAllText(path, JsonSerializer.Serialize(change(current), JsonOptions));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
