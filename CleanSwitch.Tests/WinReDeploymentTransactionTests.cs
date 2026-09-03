using System.Security.Cryptography;
using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Tests;

public sealed class WinReDeploymentTransactionTests
{
    public static IEnumerable<object[]> FaultBoundaries =>
        Enum.GetValues<WinReDeploymentFaultPoint>().Select(point => new object[] { point });

    [Theory]
    [MemberData(nameof(FaultBoundaries))]
    public async Task Process_kill_at_every_D3_D5_boundary_requires_deterministic_rollback(
        WinReDeploymentFaultPoint point)
    {
        using var fixture = DeploymentFixture.Create();
        var transaction = fixture.Transaction(new ThrowAtFault(point));

        await Assert.ThrowsAsync<InjectedPowerLossException>(() => transaction.DeployAsync(fixture.Plan));

        var interrupted = fixture.Journal.Load();
        Assert.True(interrupted.RequiresRecovery);
        Assert.NotEqual(WinReDeploymentStage.Committed, interrupted.Last.Stage);

        var recovery = fixture.Transaction().RecoverToRollbackAsync();
        var result = await recovery;

        Assert.True(result.Passed);
        Assert.Equal(WinReDeploymentStage.RolledBack, result.Stage);
        Assert.Equal(fixture.OriginalHash, Hash(fixture.Live));
        Assert.False(File.Exists(fixture.Incoming));
        Assert.True(fixture.Platform.Enabled);
        Assert.True(fixture.Platform.ProtectedBcdUnchanged);
        Assert.True(fixture.Journal.Load().IsTerminal);
    }

    [Theory]
    [InlineData(RecoveryFilesystemState.OriginalOnly)]
    [InlineData(RecoveryFilesystemState.OriginalAndPartialIncoming)]
    [InlineData(RecoveryFilesystemState.OriginalAbsentPartialIncoming)]
    [InlineData(RecoveryFilesystemState.OriginalAbsentVerifiedIncoming)]
    [InlineData(RecoveryFilesystemState.FinalPreparedNotRegistered)]
    [InlineData(RecoveryFilesystemState.RegisteredButDisabled)]
    [InlineData(RecoveryFilesystemState.EnabledUnexpectedRecoveryGuid)]
    public async Task Every_incomplete_filesystem_state_restores_exact_original(
        RecoveryFilesystemState state)
    {
        using var fixture = DeploymentFixture.Create();
        fixture.CreateInterruptedJournal(state);
        fixture.Platform.SetFilesystemState(state);

        var result = await fixture.Transaction().RecoverToRollbackAsync();

        Assert.True(result.Passed);
        Assert.Equal(fixture.OriginalHash, Hash(fixture.Live));
        Assert.True(fixture.Platform.Enabled);
        Assert.True(fixture.Platform.ProtectedBcdUnchanged);
        Assert.Equal(WinReDeploymentStage.RolledBack, fixture.Journal.Load().Last.Stage);
    }

    [Fact]
    public async Task Unexpected_recovery_guid_after_enable_is_fail_closed_and_rolls_back()
    {
        using var fixture = DeploymentFixture.Create();
        fixture.Platform.RecoveryGuid = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Transaction().DeployAsync(fixture.Plan));

        Assert.Contains("RecoveryGuid", exception.Message, StringComparison.Ordinal);
        Assert.Equal(WinReDeploymentStage.RecoveryRequired, fixture.Journal.Load().Last.Stage);
        await fixture.Transaction().RecoverToRollbackAsync();
        Assert.Equal(fixture.OriginalHash, Hash(fixture.Live));
    }

    [Fact]
    public async Task Successful_deploy_stops_at_smoke_and_requires_receipt_before_commit()
    {
        using var fixture = DeploymentFixture.Create();

        var result = await fixture.Transaction().DeployAsync(fixture.Plan);

        Assert.True(result.Passed);
        Assert.Equal(WinReDeploymentStage.AwaitingSmoke, result.Stage);
        Assert.Equal(fixture.PreparedHash, Hash(fixture.Live));
        Assert.False(fixture.Journal.Load().IsTerminal);

        var smoke = fixture.Transaction().RecordSmokeVerified(new string('A', 64));
        Assert.Equal(WinReDeploymentStage.SmokeVerified, smoke.Stage);
        Assert.False(fixture.Journal.Load().IsTerminal);
        var committed = await fixture.Transaction().CommitAfterSmokeAsync();
        Assert.Equal(WinReDeploymentStage.Committed, committed.Stage);
        Assert.True(fixture.Journal.Load().IsTerminal);
    }

    [Fact]
    public void Journal_is_hash_chained_and_truncation_or_tampering_fails_closed()
    {
        using var fixture = DeploymentFixture.Create();
        fixture.Journal.Create(fixture.Plan);
        fixture.Journal.Append(WinReDeploymentStage.D1Snapshotted, WinReJournalRecordKind.Completion, "snapshot");
        Assert.Equal(2, fixture.Journal.Load().Records.Count);

        File.AppendAllText(fixture.Journal.Path, "{truncated");
        Assert.Throws<InvalidDataException>(() => fixture.Journal.Load());
    }

    [Fact]
    public async Task Discovery_blocks_a_new_D0_when_unresolved_or_invalid_journal_exists()
    {
        using var fixture = DeploymentFixture.Create();
        fixture.Journal.Create(fixture.Plan);
        var inventory = WinReDeploymentJournalDiscovery.Inspect(fixture.JournalRoot);
        Assert.Single(inventory.Active);

        var secondPath = Path.Combine(fixture.JournalRoot, "second", "deployment-journal.ndjson");
        var second = new WinReDeploymentTransaction(
            new FileWinReDeploymentJournal(secondPath), fixture.Platform);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => second.DeployAsync(
            fixture.Plan with { TransactionId = "second" }));
        Assert.Contains("unresolved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pre_mutation_incomplete_journal_closes_without_running_live_rollback()
    {
        using var fixture = DeploymentFixture.Create();
        fixture.Journal.Create(fixture.Plan);

        var result = await fixture.Transaction().RecoverToRollbackAsync();

        Assert.Equal(WinReDeploymentStage.RolledBack, result.Stage);
        Assert.False(fixture.Platform.RollbackCalled);
        Assert.Equal(fixture.OriginalHash, Hash(fixture.Live));
    }

    [Fact]
    public void Recovery_smoke_has_no_destructive_dependency_and_writes_durable_receipt()
    {
        using var fixture = DeploymentFixture.Create();
        var environment = new FakeSmokeEnvironment(fixture.Root);
        var result = new RecoverySmokeRunner(new CleanSwitchOptions(), environment, fixture.Plan.TransactionId).Run();

        Assert.True(result.Passed);
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.Equal(result.ReceiptSha256, Hash(result.ReceiptPath!));
        var dependencyTypes = typeof(RecoverySmokeRunner)
            .GetFields(System.Reflection.BindingFlags.Instance |
                       System.Reflection.BindingFlags.NonPublic |
                       System.Reflection.BindingFlags.Public)
            .Select(field => field.FieldType.FullName ?? field.FieldType.Name)
            .ToArray();
        Assert.DoesNotContain(dependencyTypes, name =>
            name.Contains("RetirementExecutor", StringComparison.Ordinal) ||
            name.Contains("RecoveryRunner", StringComparison.Ordinal) ||
            name.Contains("DiskCommand", StringComparison.Ordinal) ||
            name.Contains("BcdCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void Smoke_receipt_must_match_guid_data_identity_version_and_payload_hashes()
    {
        using var fixture = DeploymentFixture.Create();
        var environment = new FakeSmokeEnvironment(fixture.Root);
        var result = new RecoverySmokeRunner(new CleanSwitchOptions(), environment, fixture.Plan.TransactionId).Run();

        var hash = RecoverySmokeReceiptVerifier.Verify(result.ReceiptPath!, fixture.Plan);
        Assert.Equal(result.ReceiptSha256, hash);

        var receipt = System.Text.Json.JsonSerializer.Deserialize<RecoverySmokeReceipt>(
            File.ReadAllText(result.ReceiptPath!))!;
        File.WriteAllText(result.ReceiptPath!, System.Text.Json.JsonSerializer.Serialize(
            receipt with { RecoveryGuid = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}" }));
        Assert.Throws<InvalidOperationException>(() =>
            RecoverySmokeReceiptVerifier.Verify(result.ReceiptPath!, fixture.Plan));
    }

    [Fact]
    public async Task Every_mutation_has_durable_intent_before_operation_and_completion_after_verification()
    {
        using var fixture = DeploymentFixture.Create();
        await fixture.Transaction().DeployAsync(fixture.Plan);
        var records = fixture.Journal.Load().Records;

        AssertIntentBeforeCompletion(records, WinReDeploymentStage.D3DisableIntent, WinReDeploymentStage.D3DisabledVerified);
        AssertIntentBeforeCompletion(records, WinReDeploymentStage.D4RemoveOriginalIntent, WinReDeploymentStage.D4OriginalRemoved);
        AssertIntentBeforeCompletion(records, WinReDeploymentStage.D4CopyIncomingIntent, WinReDeploymentStage.D4IncomingVerified);
        AssertIntentBeforeCompletion(records, WinReDeploymentStage.D4FinalRenameIntent, WinReDeploymentStage.D4FinalInstalled);
        AssertIntentBeforeCompletion(records, WinReDeploymentStage.D5SetReImageIntent, WinReDeploymentStage.D5SetReImageVerified);
        AssertIntentBeforeCompletion(records, WinReDeploymentStage.D5EnableIntent, WinReDeploymentStage.D5EnabledVerified);
    }

    [Fact]
    public async Task Post_smoke_verification_failure_leaves_commit_intent_and_rolls_back()
    {
        using var fixture = DeploymentFixture.Create();
        await fixture.Transaction().DeployAsync(fixture.Plan);
        fixture.Transaction().RecordSmokeVerified(new string('A', 64));
        fixture.Platform.PostSmokePasses = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Transaction().CommitAfterSmokeAsync());

        Assert.Equal(WinReDeploymentStage.CommitIntent, fixture.Journal.Load().Last.Stage);
        await fixture.Transaction().RecoverToRollbackAsync();
        Assert.Equal(fixture.OriginalHash, Hash(fixture.Live));
    }

    [Fact]
    public void Production_deployment_never_imports_or_deletes_BCD_and_never_runs_diskpart()
    {
        var source = File.ReadAllText(FindRepoFile(
            "CleanSwitch", "Recovery", "WindowsWinReDeploymentPlatform.cs"));
        Assert.DoesNotContain("/import", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/delete", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diskpart.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/export", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/disable", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/setreimage", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/enable", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Three_disposable_cycles_restore_original_hash_after_every_rollback()
    {
        for (var index = 0; index < 3; index++)
        {
            using var fixture = DeploymentFixture.Create();
            var deployed = await fixture.Transaction().DeployAsync(fixture.Plan);
            Assert.Equal(WinReDeploymentStage.AwaitingSmoke, deployed.Stage);
            fixture.Transaction().RecordSmokeVerified(new string('A', 64));
            var rolledBack = await fixture.Transaction().RecoverToRollbackAsync();
            Assert.Equal(WinReDeploymentStage.RolledBack, rolledBack.Stage);
            Assert.Equal(fixture.OriginalHash, Hash(fixture.Live));
        }
    }

    public enum RecoveryFilesystemState
    {
        OriginalOnly,
        OriginalAndPartialIncoming,
        OriginalAbsentPartialIncoming,
        OriginalAbsentVerifiedIncoming,
        FinalPreparedNotRegistered,
        RegisteredButDisabled,
        EnabledUnexpectedRecoveryGuid
    }

    private sealed class ThrowAtFault(WinReDeploymentFaultPoint point) : IWinReDeploymentFaultInjector
    {
        public void Hit(WinReDeploymentFaultPoint actual)
        {
            if (actual == point) throw new InjectedPowerLossException(actual.ToString());
        }
    }

    private sealed class InjectedPowerLossException(string message) : Exception(message);

    private sealed class FakeSmokeEnvironment(string root) : IRecoverySmokeEnvironment
    {
        public RecoverySmokeEvidence Verify(CleanSwitchOptions options) => new(
            "{11111111-1111-1111-1111-111111111111}", "1.0.0+test",
            new string('A', 64), new string('B', 64),
            "{22222222-2222-2222-2222-222222222222}", root,
            @"X:\Windows", @"X:\CleanSwitchRecovery");

        public string WriteReceiptDurably(string recoveryDataRoot, RecoverySmokeReceipt receipt)
        {
            var path = Path.Combine(recoveryDataRoot, "receipt.json");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            System.Text.Json.JsonSerializer.Serialize(stream, receipt);
            stream.Flush(true);
            return path;
        }
    }

    private sealed class DeploymentFixture : IDisposable
    {
        private DeploymentFixture(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
            Live = Path.Combine(root, "recovery", "Winre.wim");
            Incoming = Live + ".incoming";
            Prepared = Path.Combine(root, "prepared", "Winre.wim");
            PreparedBundle = Path.Combine(root, "prepared", "prepared-winre-bundle.json");
            Backup = Path.Combine(root, "archive", "original", "Winre.wim");
            Directory.CreateDirectory(Path.GetDirectoryName(Live)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Prepared)!);
            File.WriteAllText(Live, "original-stock-winre");
            File.WriteAllText(Prepared, "prepared-cleanswitch-winre");
            File.WriteAllText(PreparedBundle, "sealed fixture bundle");
            OriginalHash = Hash(Live);
            PreparedHash = Hash(Prepared);
            JournalRoot = Path.Combine(root, "journals");
            Journal = new FileWinReDeploymentJournal(Path.Combine(JournalRoot, "one", "deployment-journal.ndjson"));
            Plan = new WinReDeploymentPlan
            {
                TransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                PreparedWimPath = Prepared,
                PreparedWimSha256 = PreparedHash,
                PreparedBundlePath = PreparedBundle,
                PreparedBundleSha256 = Hash(PreparedBundle),
                LiveWimPath = Live,
                OriginalWimSha256 = OriginalHash,
                BackupWimPath = Backup,
                IncomingWimPath = Incoming,
                RecoveryDirectory = Path.GetDirectoryName(Live)!,
                ExpectedRecoveryGuid = "{11111111-1111-1111-1111-111111111111}",
                Boot2Guid = "{22222222-2222-2222-2222-222222222222}",
                RetirementStateSha256 = new string('C', 64),
                ProtectedBcdFingerprint = new string('D', 64),
                GptLayoutFingerprint = new string('E', 64),
                RecoveryPartitionGptId = "{33333333-3333-3333-3333-333333333333}",
                RecoveryDataVolumeGptId = "{22222222-2222-2222-2222-222222222222}",
                ProductVersion = "1.0.0+test",
                ExecutableSha256 = new string('A', 64),
                ConfigurationSha256 = new string('B', 64)
            };
            Platform = new FakePlatform(this);
        }

        public string Root { get; }
        public string Live { get; }
        public string Incoming { get; }
        public string Prepared { get; }
        public string PreparedBundle { get; }
        public string Backup { get; }
        public string OriginalHash { get; }
        public string PreparedHash { get; }
        public string JournalRoot { get; }
        public FileWinReDeploymentJournal Journal { get; }
        public WinReDeploymentPlan Plan { get; }
        public FakePlatform Platform { get; }

        public static DeploymentFixture Create() => new(Path.Combine(
            Path.GetTempPath(), "CleanSwitch-WinRE-Deploy-" + Guid.NewGuid().ToString("N")));

        public WinReDeploymentTransaction Transaction(IWinReDeploymentFaultInjector? faults = null) =>
            new(Journal, Platform, faults);

        public void CreateInterruptedJournal(RecoveryFilesystemState state)
        {
            Journal.Create(Plan);
            Journal.Append(StageFor(state), WinReJournalRecordKind.Intent, "simulated interruption");
        }

        private static WinReDeploymentStage StageFor(RecoveryFilesystemState state) => state switch
        {
            RecoveryFilesystemState.OriginalOnly => WinReDeploymentStage.D3DisableIntent,
            RecoveryFilesystemState.OriginalAndPartialIncoming => WinReDeploymentStage.D4CopyIncomingIntent,
            RecoveryFilesystemState.OriginalAbsentPartialIncoming => WinReDeploymentStage.D4CopyIncomingIntent,
            RecoveryFilesystemState.OriginalAbsentVerifiedIncoming => WinReDeploymentStage.D4IncomingVerified,
            RecoveryFilesystemState.FinalPreparedNotRegistered => WinReDeploymentStage.D5SetReImageIntent,
            RecoveryFilesystemState.RegisteredButDisabled => WinReDeploymentStage.D5EnableIntent,
            _ => WinReDeploymentStage.RecoveryRequired
        };

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FakePlatform(DeploymentFixture fixture) : IWinReDeploymentPlatform
    {
        public bool Enabled { get; set; } = true;
        public bool Registered { get; set; } = true;
        public bool ProtectedBcdUnchanged { get; set; } = true;
        public string RecoveryGuid { get; set; } = fixture.Plan.ExpectedRecoveryGuid;
        public bool RollbackCalled { get; private set; }
        public bool PostSmokePasses { get; set; } = true;

        public Task<WinReDeploymentVerification> VerifyD0Async(WinReDeploymentPlan plan) => Ok();
        public Task<WinReDeploymentVerification> CaptureSnapshotsAsync(WinReDeploymentPlan plan) => Ok();
        public Task<WinReDeploymentVerification> BackupOriginalAsync(WinReDeploymentPlan plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.BackupWimPath)!);
            File.Copy(plan.LiveWimPath, plan.BackupWimPath);
            return Ok();
        }
        public Task DisableAsync(WinReDeploymentPlan plan) { Enabled = false; return Task.CompletedTask; }
        public Task<WinReDeploymentVerification> VerifyDisabledAsync(WinReDeploymentPlan plan) => Result(!Enabled);
        public Task RemoveOriginalAsync(WinReDeploymentPlan plan) { File.Delete(plan.LiveWimPath); return Task.CompletedTask; }
        public Task<WinReDeploymentVerification> VerifyOriginalRemovedAsync(WinReDeploymentPlan plan) => Result(!File.Exists(plan.LiveWimPath));
        public async Task CopyIncomingAsync(WinReDeploymentPlan plan, Action duringCopy)
        {
            await using var output = File.Create(plan.IncomingWimPath);
            var bytes = File.ReadAllBytes(plan.PreparedWimPath);
            await output.WriteAsync(bytes.AsMemory(0, Math.Max(1, bytes.Length / 2)));
            await output.FlushAsync();
            duringCopy();
            await output.WriteAsync(bytes.AsMemory(Math.Max(1, bytes.Length / 2)));
        }
        public Task<WinReDeploymentVerification> VerifyIncomingAsync(WinReDeploymentPlan plan) => Result(Hash(plan.IncomingWimPath) == plan.PreparedWimSha256);
        public Task ActivateIncomingAsync(WinReDeploymentPlan plan) { File.Move(plan.IncomingWimPath, plan.LiveWimPath); return Task.CompletedTask; }
        public Task<WinReDeploymentVerification> VerifyFinalInstalledAsync(WinReDeploymentPlan plan) => Result(Hash(plan.LiveWimPath) == plan.PreparedWimSha256);
        public Task SetReImageAsync(WinReDeploymentPlan plan) { Registered = true; return Task.CompletedTask; }
        public Task<WinReDeploymentVerification> VerifySetReImageAsync(WinReDeploymentPlan plan) => Result(Registered && !Enabled);
        public Task EnableAsync(WinReDeploymentPlan plan) { Enabled = true; return Task.CompletedTask; }
        public Task<WinReDeploymentVerification> VerifyEnabledAsync(WinReDeploymentPlan plan) =>
            Task.FromResult(new WinReDeploymentVerification(Enabled && Registered && ProtectedBcdUnchanged, "enabled", RecoveryGuid));
        public Task<WinReDeploymentVerification> ReviewLauncherAsync(WinReDeploymentPlan plan) => Ok();
        public Task<WinReDeploymentVerification> VerifyPostSmokeAsync(WinReDeploymentPlan plan) => Result(PostSmokePasses);
        public Task RollbackAsync(WinReDeploymentPlan plan)
        {
            RollbackCalled = true;
            Enabled = false;
            if (File.Exists(plan.IncomingWimPath)) File.Delete(plan.IncomingWimPath);
            if (File.Exists(plan.LiveWimPath)) File.Delete(plan.LiveWimPath);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.LiveWimPath)!);
            File.Copy(plan.BackupWimPath, plan.LiveWimPath);
            Registered = true;
            Enabled = true;
            ProtectedBcdUnchanged = true;
            return Task.CompletedTask;
        }
        public Task<WinReDeploymentVerification> VerifyRollbackAsync(WinReDeploymentPlan plan) =>
            Result(Hash(plan.LiveWimPath) == plan.OriginalWimSha256 && Enabled && ProtectedBcdUnchanged);

        public void SetFilesystemState(RecoveryFilesystemState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Backup)!);
            File.Copy(fixture.Live, fixture.Backup, overwrite: true);
            switch (state)
            {
                case RecoveryFilesystemState.OriginalOnly:
                    break;
                case RecoveryFilesystemState.OriginalAndPartialIncoming:
                    File.WriteAllText(fixture.Incoming, "partial");
                    break;
                case RecoveryFilesystemState.OriginalAbsentPartialIncoming:
                    File.Delete(fixture.Live);
                    File.WriteAllText(fixture.Incoming, "partial");
                    break;
                case RecoveryFilesystemState.OriginalAbsentVerifiedIncoming:
                    File.Delete(fixture.Live);
                    File.Copy(fixture.Prepared, fixture.Incoming);
                    break;
                case RecoveryFilesystemState.FinalPreparedNotRegistered:
                    File.Copy(fixture.Prepared, fixture.Live, overwrite: true);
                    Registered = false;
                    Enabled = false;
                    break;
                case RecoveryFilesystemState.RegisteredButDisabled:
                    File.Copy(fixture.Prepared, fixture.Live, overwrite: true);
                    Registered = true;
                    Enabled = false;
                    break;
                case RecoveryFilesystemState.EnabledUnexpectedRecoveryGuid:
                    File.Copy(fixture.Prepared, fixture.Live, overwrite: true);
                    Registered = true;
                    Enabled = true;
                    RecoveryGuid = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";
                    break;
            }
        }

        private static Task<WinReDeploymentVerification> Ok() => Result(true);
        private static Task<WinReDeploymentVerification> Result(bool pass) =>
            Task.FromResult(new WinReDeploymentVerification(pass, pass ? "PASS" : "FAIL"));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AssertIntentBeforeCompletion(
        IReadOnlyList<WinReDeploymentJournalRecord> records,
        WinReDeploymentStage intent,
        WinReDeploymentStage completion)
    {
        var intentRecord = Assert.Single(records, record => record.Stage == intent);
        var completionRecord = Assert.Single(records, record => record.Stage == completion);
        Assert.Equal(WinReJournalRecordKind.Intent, intentRecord.Kind);
        Assert.Equal(WinReJournalRecordKind.Completion, completionRecord.Kind);
        Assert.True(intentRecord.Sequence < completionRecord.Sequence);
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

}
