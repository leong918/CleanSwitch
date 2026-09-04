using System.Security.Cryptography;
using System.Text;
using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests;

public sealed class RetirementAbandonerTests
{
    public const string ExpectedSupersedeReason =
        "Operator abandoned a stale PENDING retirement operation so a fresh retirement state can be captured.";

    [Fact]
    public void Pending_schema_v1_can_be_explicitly_abandoned()
    {
        using var workspace = new TempAbandonWorkspace();
        var created = new DateTimeOffset(2026, 8, 29, 3, 13, 39, TimeSpan.Zero);
        workspace.SeedPending(schemaVersion: 1, createdAtUtc: created);

        var result = workspace.CreateAbandoner().Execute();

        AssertSuccessfulAbandon(workspace, result, schemaVersion: 1, created);
        Assert.DoesNotContain("boot1BcdObjectId", File.ReadAllText(result.ArchivePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pending_schema_v2_can_be_explicitly_abandoned()
    {
        using var workspace = new TempAbandonWorkspace();
        var created = new DateTimeOffset(2026, 9, 1, 22, 57, 46, TimeSpan.Zero);
        workspace.SeedPending(schemaVersion: 2, createdAtUtc: created);

        var result = workspace.CreateAbandoner().Execute();

        AssertSuccessfulAbandon(workspace, result, schemaVersion: 2, created);
        Assert.Contains("boot1BcdObjectId", File.ReadAllText(result.ArchivePath), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RetirementStatus.Failed)]
    public void Other_supported_operator_cleanup_states_can_be_abandoned(RetirementStatus status)
    {
        using var workspace = new TempAbandonWorkspace();
        workspace.SeedTerminal(status, schemaVersion: 2);

        var result = workspace.CreateAbandoner().Execute();

        Assert.Equal(RetirementStatus.Aborted, result.Reloaded.Status);
        Assert.True(result.Reloaded.IsTerminal);
        Assert.True(File.Exists(result.ArchivePath));
        Assert.False(result.Reloaded.DestructiveDeletionPerformed);
        Assert.False(result.Reloaded.BcdDeletionPerformed);
    }

    [Fact]
    public void Boot1_retired_cannot_be_abandoned_after_destructive_boundary()
    {
        using var workspace = new TempAbandonWorkspace();
        var seeded = workspace.SeedTerminal(RetirementStatus.Boot1Retired, schemaVersion: 2);
        seeded.DestructiveDeletionPerformed = true;
        workspace.Coordinator.Persist(seeded);

        var exception = Assert.Throws<RetirementStateException>(() => workspace.CreateAbandoner().Execute());

        Assert.Contains("BOOT1_RETIRED -> ABORTED", exception.Message, StringComparison.Ordinal);
        var reloaded = workspace.Coordinator.TryLoad();
        Assert.NotNull(reloaded);
        Assert.Equal(RetirementStatus.Boot1Retired, reloaded.Status);
        Assert.True(reloaded.DestructiveDeletionPerformed);
    }

    [Fact]
    public void Archive_is_created_first_and_keeps_the_pending_bytes()
    {
        using var workspace = new TempAbandonWorkspace();
        workspace.SeedPending(schemaVersion: 1);
        var originalBytes = File.ReadAllBytes(workspace.Store.StateFilePath);
        var originalHash = Sha256Hex(originalBytes);

        var failingCoordinator = new AbortFailingCoordinator(workspace.Coordinator);
        var abandoner = new RetirementAbandoner(failingCoordinator, workspace.Log);

        var exception = Assert.Throws<RetirementStateException>(() => abandoner.Execute());

        Assert.Contains("injected MarkAborted failure", exception.Message, StringComparison.Ordinal);
        var archives = workspace.ArchiveFiles();
        Assert.Single(archives);
        Assert.True(File.Exists(archives[0].FullName));
        Assert.Equal(originalHash, Sha256Hex(File.ReadAllBytes(archives[0].FullName)));
        Assert.Equal(originalHash, Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath)));
        Assert.Equal(RetirementStatus.Pending, workspace.Coordinator.TryLoad()!.Status);
    }

    [Fact]
    public void Successful_abandon_archives_pending_bytes_before_aborted_write()
    {
        using var workspace = new TempAbandonWorkspace();
        workspace.SeedPending(schemaVersion: 1);
        var originalHash = Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath));

        var result = workspace.CreateAbandoner().Execute();

        Assert.True(File.Exists(result.ArchivePath));
        Assert.Equal(originalHash, result.ArchiveSha256Hex);
        Assert.Equal(originalHash, Sha256Hex(File.ReadAllBytes(result.ArchivePath)));
        Assert.NotEqual(originalHash, Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath)));
        Assert.Contains("\"status\": \"PENDING\"", File.ReadAllText(result.ArchivePath), StringComparison.Ordinal);
        Assert.Contains("\"status\": \"ABORTED\"", File.ReadAllText(workspace.Store.StateFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_verification_failure_prevents_aborted_write()
    {
        using var workspace = new TempAbandonWorkspace();
        workspace.SeedPending(schemaVersion: 2);
        var originalHash = Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath));
        var calls = 0;
        var archiver = new FileRetirementStateArchiver(_ =>
        {
            calls++;
            return calls == 1 ? "AAA" : "BBB";
        });

        var exception = Assert.Throws<RetirementStateException>(() =>
            workspace.CreateAbandoner(archiver).Execute());

        Assert.Contains("Archive verification failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("live retirement state was not changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(RetirementStatus.Pending, workspace.Coordinator.TryLoad()!.Status);
        Assert.Equal(originalHash, Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath)));
        Assert.True(workspace.ArchiveFiles().Length > 0);
        Assert.DoesNotContain("\"status\": \"ABORTED\"", File.ReadAllText(workspace.Store.StateFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Real_hash_mismatch_verify_throws_and_keeps_archive()
    {
        using var workspace = new TempAbandonWorkspace();
        var source = Path.Combine(workspace.Root, "source.json");
        var dest = Path.Combine(workspace.Root, "dest.json");
        File.WriteAllText(source, "{\"status\":\"PENDING\"}", new UTF8Encoding(false));
        File.Copy(source, dest);
        File.AppendAllText(dest, " ");

        var exception = Assert.Throws<RetirementStateException>(() =>
            FileRetirementStateArchiver.VerifyIdentical(source, dest));

        Assert.Contains("Archive verification failed", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public void Complete_cannot_be_abandoned()
    {
        using var workspace = new TempAbandonWorkspace();
        workspace.SeedTerminal(RetirementStatus.Complete, schemaVersion: 2);
        var originalHash = Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath));

        var exception = Assert.Throws<RetirementStateException>(() => workspace.CreateAbandoner().Execute());

        Assert.Contains("COMPLETE", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalHash, Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath)));
        Assert.Empty(workspace.ArchiveFiles());
        Assert.Equal(RetirementStatus.Complete, workspace.Coordinator.TryLoad()!.Status);
    }

    [Fact]
    public void Aborted_cannot_be_abandoned_again()
    {
        using var workspace = new TempAbandonWorkspace();
        workspace.SeedPending(schemaVersion: 1);
        var first = workspace.CreateAbandoner().Execute();
        Assert.Equal(RetirementStatus.Aborted, first.Reloaded.Status);
        var hashAfterFirst = Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath));

        var exception = Assert.Throws<RetirementStateException>(() => workspace.CreateAbandoner().Execute());

        Assert.Contains("already ABORTED", exception.Message, StringComparison.Ordinal);
        Assert.Equal(hashAfterFirst, Sha256Hex(File.ReadAllBytes(workspace.Store.StateFilePath)));
        Assert.Single(workspace.ArchiveFiles());
    }

    [Fact]
    public void Missing_state_refuses()
    {
        using var workspace = new TempAbandonWorkspace();

        var exception = Assert.Throws<RetirementStateException>(() => workspace.CreateAbandoner().Execute());

        Assert.Contains("No retirement state file was found", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(workspace.Store.StateFilePath));
        Assert.Empty(workspace.ArchiveFiles());
    }

    [Fact]
    public void Malformed_state_refuses()
    {
        using var workspace = new TempAbandonWorkspace();
        const string malformed = "{ this is not retirement-state json";
        File.WriteAllText(workspace.Store.StateFilePath, malformed, new UTF8Encoding(false));

        var exception = Assert.Throws<RetirementStorageException>(() => workspace.CreateAbandoner().Execute());

        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
        Assert.Equal(malformed, File.ReadAllText(workspace.Store.StateFilePath));
        Assert.Empty(workspace.ArchiveFiles());
        Assert.DoesNotContain("ABORTED", File.ReadAllText(workspace.Store.StateFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void After_abandon_BeginRetirement_is_allowed_and_abandon_does_not_start_one()
    {
        using var workspace = new TempAbandonWorkspace();
        workspace.SeedPending(schemaVersion: 1);
        var result = workspace.CreateAbandoner().Execute();

        Assert.True(result.BeginRetirementAllowed);
        Assert.True(RetirementAbandoner.AllowsNewRetirement(result.Reloaded));
        Assert.Equal(RetirementStatus.Aborted, workspace.Coordinator.TryLoad()!.Status);

        var fresh = workspace.Coordinator.BeginRetirement(
            BcdIdentifiers.Format(BcdFixtures.Boot1),
            BcdIdentifiers.Format(BcdFixtures.Boot2),
            BcdIdentifiers.Format(BcdFixtures.Recovery),
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity());

        Assert.Equal(RetirementStatus.Pending, fresh.Status);
        Assert.Equal(RetirementState.CurrentSchemaVersion, fresh.SchemaVersion);
        Assert.True(File.Exists(result.ArchivePath));
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(result.ArchivePath), StringComparison.Ordinal);
    }

    [Fact]
    public void No_destructive_command_interfaces_are_invoked()
    {
        using var workspace = new TempAbandonWorkspace();
        workspace.SeedPending(schemaVersion: 2);
        var disk = new FakeDestructiveDiskCommand();
        var bcd = new FakeDestructiveBcdCommand();

        workspace.CreateAbandoner().Execute();

        Assert.Equal(0, disk.ExecuteCount);
        Assert.Equal(0, bcd.ExecuteCount);
        Assert.Null(disk.LastTarget);
        Assert.Null(bcd.LastTarget);

        var abandonerSource = ReadRepoFile("CleanSwitch", "Services", "RetirementAbandoner.cs");
        var archiverSource = ReadRepoFile("CleanSwitch", "Services", "FileRetirementStateArchiver.cs");
        var commandSource = ReadRepoFile("CleanSwitch", "RetirementAbandonCommand.cs");

        Assert.Contains("RetirementStateAccessContext.OperatorAbandon", commandSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowStateOnSystemVolume = true", commandSource, StringComparison.Ordinal);

        foreach (var source in new[] { abandonerSource, archiverSource, commandSource })
        {
            Assert.DoesNotContain("IDestructiveDiskCommand", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IDestructiveBcdCommand", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DiskpartDestructiveDiskCommand", source, StringComparison.Ordinal);
            Assert.DoesNotContain("BcdeditDestructiveBcdCommand", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".BeginRetirement(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_coordinator.BeginRetirement", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RestartAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RetirementServices.Create", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new RetirementExecutor", source, StringComparison.Ordinal);
        }

        Assert.Contains("--abandon-retirement", ReadRepoFile("CleanSwitch", "Program.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Abandon_succeeds_when_state_is_on_running_system_volume_without_appsettings_override()
    {
        var folder = CreateSystemVolumeTestFolder();
        try
        {
            var options = new CleanSwitchOptions
            {
                RecoveryDataPath = folder,
                StateFileName = "retirement-state-test.json",
                AllowStateOnSystemVolume = false
            };
            var log = new RecordingOperationLog();
            var created = new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);
            var pending = new RetirementState
            {
                Operation = RetirementState.RetireBoot1Operation,
                Status = RetirementStatus.Pending,
                SchemaVersion = 2,
                Phase = "2B-identify",
                CreatedAtUtc = created,
                UpdatedAtUtc = created,
                Boot1Id = BcdIdentifiers.Format(BcdFixtures.Boot1),
                Boot2Id = BcdIdentifiers.Format(BcdFixtures.Boot2),
                RecoveryId = BcdIdentifiers.Format(BcdFixtures.Recovery),
                Boot1BcdObjectId = BcdIdentifiers.Format(BcdFixtures.Boot1),
                Boot2BcdObjectId = BcdIdentifiers.Format(BcdFixtures.Boot2),
                MachineName = "TEST-PC",
                Boot1Identity = RetirementFixtures.Boot1Identity(),
                Boot2Identity = RetirementFixtures.Boot2Identity(),
                Transitions =
                [
                    new RetirementTransition
                    {
                        From = RetirementStatus.Pending,
                        To = RetirementStatus.Pending,
                        AtUtc = created,
                        Reason = "seed"
                    }
                ]
            };

            // Arrange a pre-existing file with a separate test-only writer. The production
            // operator-abandon context itself is deliberately unable to persist PENDING.
            var seedStore = new RetirementStateStore(
                new CleanSwitchOptions
                {
                    RecoveryDataPath = folder,
                    StateFileName = options.StateFileName,
                    AllowStateOnSystemVolume = true
                },
                log);
            seedStore.Save(pending);

            var store = new RetirementStateStore(
                options,
                log,
                RetirementStateAccessContext.OperatorAbandon);
            store.EnsureWritable();
            Assert.Throws<RetirementStorageException>(() => store.Save(pending));
            var coordinator = new RetirementCoordinator(store, log);

            var result = new RetirementAbandoner(coordinator, log).Execute();

            Assert.Equal(RetirementStatus.Aborted, result.Reloaded.Status);
            Assert.True(result.Reloaded.IsTerminal);
            Assert.True(File.Exists(result.ArchivePath));
        }
        finally
        {
            TryDeleteDirectory(folder);
        }
    }

    [Fact]
    public void Normal_retirement_services_reject_state_on_running_system_volume()
    {
        var folder = CreateSystemVolumeTestFolder();
        try
        {
            var options = new CleanSwitchOptions
            {
                RecoveryDataPath = folder,
                StateFileName = "retirement-state-test.json",
                AllowStateOnSystemVolume = false
            };

            var exception = Assert.Throws<RetirementStorageException>(() =>
                RetirementServices.CreateForNewOperation(options, "test"));

            Assert.Contains("being retired", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(folder);
        }
    }

    [Fact]
    public void Operator_abandon_context_is_scoped_and_does_not_enable_appsettings_override()
    {
        var folder = CreateSystemVolumeTestFolder();
        try
        {
            var defaultOptions = new CleanSwitchOptions
            {
                RecoveryDataPath = folder,
                StateFileName = "retirement-state-test.json",
                AllowStateOnSystemVolume = false
            };

            Assert.Throws<RetirementStorageException>(() =>
            {
                var store = new RetirementStateStore(defaultOptions, new RecordingOperationLog());
                store.EnsureWritable();
            });

            var abandonOptions = new CleanSwitchOptions
            {
                RecoveryDataPath = folder,
                StateFileName = "retirement-state-test.json",
                AllowStateOnSystemVolume = false
            };
            Assert.False(abandonOptions.AllowStateOnSystemVolume);
            Assert.False(defaultOptions.AllowStateOnSystemVolume);

            var store = new RetirementStateStore(
                abandonOptions,
                new RecordingOperationLog(),
                RetirementStateAccessContext.OperatorAbandon);
            store.EnsureWritable();
        }
        finally
        {
            TryDeleteDirectory(folder);
        }
    }

    [Fact]
    public void Default_appsettings_keep_allow_state_on_system_volume_false()
    {
        var settings = ReadRepoFile("CleanSwitch", "appsettings.json");
        Assert.Contains("\"AllowStateOnSystemVolume\": false", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AllowStateOnSystemVolume\": true", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_destructive_flags_remain_false()
    {
        var gates = ReadRepoFile("CleanSwitch", "Recovery", "ProductionRetirementGates.cs");
        Assert.Contains("DestructiveOperationsImplemented =", gates, StringComparison.Ordinal);
        Assert.Contains("BcdOperationsImplemented =", gates, StringComparison.Ordinal);
#if CLEANSWITCH_LIVE_TEST_BUILD
        Assert.Contains("true;", gates, StringComparison.Ordinal);
#else
        Assert.Contains("false;", gates, StringComparison.Ordinal);
#endif

        var settings = ReadRepoFile("CleanSwitch", "appsettings.json");
        Assert.Contains("\"EnableDestructiveRetirement\": false", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EnableDestructiveRetirement\": true", settings, StringComparison.Ordinal);
    }

    private static void AssertSuccessfulAbandon(
        TempAbandonWorkspace workspace,
        RetirementAbandonResult result,
        int schemaVersion,
        DateTimeOffset created)
    {
        Assert.Equal(ExpectedSupersedeReason, result.Reason);
        Assert.Equal(ExpectedSupersedeReason, RetirementAbandoner.DefaultSupersedeReason);
        Assert.Equal(schemaVersion, result.Original.SchemaVersion);
        Assert.Equal(RetirementStatus.Pending, result.Original.Status);
        Assert.Equal(created, result.OriginalCreatedAtUtc);
        Assert.Equal(created, result.Reloaded.CreatedAtUtc);
        Assert.Equal(RetirementStatus.Aborted, result.Reloaded.Status);
        Assert.True(result.Reloaded.IsTerminal);
        Assert.True(result.BeginRetirementAllowed);
        Assert.True(File.Exists(result.ArchivePath));
        Assert.Contains(
            $"retirement-state.PENDING.v{schemaVersion}.",
            Path.GetFileName(result.ArchivePath),
            StringComparison.Ordinal);
        Assert.StartsWith(
            Path.Combine(workspace.Store.StateDirectory, FileRetirementStateArchiver.ArchiveFolderName),
            result.ArchivePath,
            StringComparison.OrdinalIgnoreCase);

        var last = result.Reloaded.Transitions[^1];
        Assert.Equal(RetirementStatus.Pending, last.From);
        Assert.Equal(RetirementStatus.Aborted, last.To);
        Assert.Equal(ExpectedSupersedeReason, last.Reason);
        Assert.Equal(last.AtUtc, result.AbortedAtUtc);
        Assert.True(result.Reloaded.UpdatedAtUtc >= created);

        var archiveJson = File.ReadAllText(result.ArchivePath);
        Assert.Contains("\"status\": \"PENDING\"", archiveJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\": \"ABORTED\"", archiveJson, StringComparison.Ordinal);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string CreateSystemVolumeTestFolder()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)
            ?? throw new InvalidOperationException("System root unavailable.");
        var folder = Path.Combine(root, $"cs-sysvol-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDeleteDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        return File.ReadAllText(Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(relative).ToArray())));
    }

    private sealed class AbortFailingCoordinator : IRetirementCoordinator
    {
        private readonly RetirementCoordinator _inner;

        public AbortFailingCoordinator(RetirementCoordinator inner) => _inner = inner;

        public string StateFilePath => _inner.StateFilePath;

        public void EnsureStorageReady() => _inner.EnsureStorageReady();

        public RetirementState? TryLoad() => _inner.TryLoad();

        public RetirementState BeginRetirement(
            string boot1Id,
            string boot2Id,
            string recoveryId,
            PartitionIdentity boot1Identity,
            PartitionIdentity boot2Identity) =>
            _inner.BeginRetirement(boot1Id, boot2Id, recoveryId, boot1Identity, boot2Identity);

        public RetirementState Transition(RetirementState state, RetirementStatus target, string reason) =>
            _inner.Transition(state, target, reason);

        public RetirementState RecordBoot1Retired(RetirementState state, string reason, bool deletionOccurred) =>
            _inner.RecordBoot1Retired(state, reason, deletionOccurred);

        public RetirementState MarkFailed(RetirementState state, string error) =>
            _inner.MarkFailed(state, error);

        public RetirementState MarkAborted(RetirementState state, string reason) =>
            throw new RetirementStateException("injected MarkAborted failure");

        public RetirementState Persist(RetirementState state) => _inner.Persist(state);

        public RetirementState? TryCompleteAfterReboot(string currentBootGuid) =>
            _inner.TryCompleteAfterReboot(currentBootGuid);
    }

    private sealed class TempAbandonWorkspace : IDisposable
    {
        public TempAbandonWorkspace()
        {
            FolderName = $"cs-abandon-{Guid.NewGuid():N}";
            Root = Path.Combine(Path.GetTempPath(), FolderName);
            Directory.CreateDirectory(Root);
            Log = new RecordingOperationLog();
            Store = new RetirementStateStore(
                new CleanSwitchOptions
                {
                    RecoveryDataPath = Root,
                    RecoveryDataFolderName = FolderName,
                    StateFileName = "retirement-state-test.json",
                    AllowStateOnSystemVolume = true
                },
                Log);
            Store.EnsureWritable();
            Coordinator = new RetirementCoordinator(Store, Log);
        }

        public string Root { get; }

        public string FolderName { get; }

        public RecordingOperationLog Log { get; }

        public RetirementStateStore Store { get; }

        public RetirementCoordinator Coordinator { get; }

        public RetirementAbandoner CreateAbandoner(IRetirementStateArchiver? archiver = null) =>
            new(Coordinator, Log, archiver);

        public FileInfo[] ArchiveFiles()
        {
            var dir = Path.Combine(Store.StateDirectory, FileRetirementStateArchiver.ArchiveFolderName);
            return Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("*.json", SearchOption.TopDirectoryOnly)
                : [];
        }

        public RetirementState SeedPending(int schemaVersion, DateTimeOffset? createdAtUtc = null)
        {
            var created = createdAtUtc ?? new DateTimeOffset(2026, 8, 29, 3, 13, 39, TimeSpan.Zero);
            if (schemaVersion <= 1)
            {
                WriteRawV1Pending(created);
                return Coordinator.TryLoad() ?? throw new InvalidOperationException("seed failed");
            }

            var state = BaseState(schemaVersion, RetirementStatus.Pending, created);
            Store.Save(state);
            RewriteCreatedAt(created);
            return Coordinator.TryLoad() ?? throw new InvalidOperationException("seed failed");
        }

        private void WriteRawV1Pending(DateTimeOffset created)
        {
            var stamp = created.ToString("o");
            var json =
                "{" + Environment.NewLine +
                "  \"operation\": \"RETIRE_BOOT1\"," + Environment.NewLine +
                "  \"status\": \"PENDING\"," + Environment.NewLine +
                $"  \"boot1Id\": \"{BcdIdentifiers.Format(BcdFixtures.Boot1)}\"," + Environment.NewLine +
                $"  \"boot2Id\": \"{BcdIdentifiers.Format(BcdFixtures.Boot2)}\"," + Environment.NewLine +
                $"  \"recoveryId\": \"{BcdIdentifiers.Format(BcdFixtures.Recovery)}\"," + Environment.NewLine +
                $"  \"createdAtUtc\": \"{stamp}\"," + Environment.NewLine +
                $"  \"updatedAtUtc\": \"{stamp}\"," + Environment.NewLine +
                "  \"schemaVersion\": 1," + Environment.NewLine +
                "  \"phase\": \"2A\"," + Environment.NewLine +
                "  \"destructiveDeletionPerformed\": false," + Environment.NewLine +
                "  \"machineName\": \"TEST-PC\"," + Environment.NewLine +
                "  \"transitions\": [" + Environment.NewLine +
                "    {" + Environment.NewLine +
                "      \"from\": \"PENDING\"," + Environment.NewLine +
                "      \"to\": \"PENDING\"," + Environment.NewLine +
                $"      \"atUtc\": \"{stamp}\"," + Environment.NewLine +
                "      \"reason\": \"seed\"" + Environment.NewLine +
                "    }" + Environment.NewLine +
                "  ]" + Environment.NewLine +
                "}";
            File.WriteAllText(Store.StateFilePath, json, new UTF8Encoding(false));
        }

        public RetirementState SeedTerminal(RetirementStatus status, int schemaVersion)
        {
            var created = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            var state = BaseState(schemaVersion, status, created);
            state.Transitions.Add(new RetirementTransition
            {
                From = RetirementStatus.Pending,
                To = status,
                AtUtc = created.AddHours(1),
                Reason = "seed terminal"
            });
            Store.Save(state);
            return Coordinator.TryLoad() ?? throw new InvalidOperationException("seed failed");
        }

        private void RewriteCreatedAt(DateTimeOffset created)
        {
            var json = File.ReadAllText(Store.StateFilePath);
            json = System.Text.RegularExpressions.Regex.Replace(
                json,
                "\"createdAtUtc\":\\s*\"[^\"]+\"",
                $"\"createdAtUtc\": \"{created:o}\"");
            File.WriteAllText(Store.StateFilePath, json, new UTF8Encoding(false));
        }

        private static RetirementState BaseState(int schemaVersion, RetirementStatus status, DateTimeOffset created) =>
            new()
            {
                Operation = RetirementState.RetireBoot1Operation,
                Status = status,
                SchemaVersion = schemaVersion,
                Phase = schemaVersion <= 1 ? "2A" : "2B-identify",
                CreatedAtUtc = created,
                UpdatedAtUtc = created,
                Boot1Id = BcdIdentifiers.Format(BcdFixtures.Boot1),
                Boot2Id = BcdIdentifiers.Format(BcdFixtures.Boot2),
                RecoveryId = BcdIdentifiers.Format(BcdFixtures.Recovery),
                Boot1BcdObjectId = schemaVersion >= 2 ? BcdIdentifiers.Format(BcdFixtures.Boot1) : null,
                Boot2BcdObjectId = schemaVersion >= 2 ? BcdIdentifiers.Format(BcdFixtures.Boot2) : null,
                DestructiveDeletionPerformed = false,
                MachineName = "TEST-PC",
                Boot1Identity = schemaVersion >= 2 ? RetirementFixtures.Boot1Identity() : null,
                Boot2Identity = schemaVersion >= 2 ? RetirementFixtures.Boot2Identity() : null,
                Transitions =
                [
                    new RetirementTransition
                    {
                        From = RetirementStatus.Pending,
                        To = RetirementStatus.Pending,
                        AtUtc = created,
                        Reason = "seed"
                    }
                ]
            };

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
