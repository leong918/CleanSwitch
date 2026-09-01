using System.Security.Principal;
using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support.Vhd;

internal sealed class DisposableVhdSession : IDisposable
{
    private bool _attached;
    private bool _disposed;

    private DisposableVhdSession(string vhdxPath)
    {
        VhdxPath = vhdxPath;
    }

    public string VhdxPath { get; }

    public int DiskNumber { get; private set; }

    public Guid DiskGptId { get; private set; }

    public LivePartition Efi { get; private set; } = null!;

    public LivePartition Msr { get; private set; } = null!;

    public LivePartition Boot1 { get; private set; } = null!;

    public LivePartition Boot1Recovery { get; private set; } = null!;

    public LivePartition Boot2 { get; private set; } = null!;

    public LivePartition Boot2Recovery { get; private set; } = null!;

    public VirtualDiskProof Proof { get; private set; } = null!;

    public RetirementIdentitySet Identities => new()
    {
        Boot1GptId = Boot1.PartitionGptId,
        Boot2GptId = Boot2.PartitionGptId,
        Boot2Disk = Boot2.DiskNumber,
        Boot2Partition = Boot2.PartitionNumber,
        ProtectedGptIds =
        [
            Efi.PartitionGptId,
            Msr.PartitionGptId,
            Boot2.PartitionGptId,
            Boot1Recovery.PartitionGptId,
            Boot2Recovery.PartitionGptId
        ]
    };

    public PartitionIdentity Boot1Identity => ToIdentity(Boot1, "VHD integration test fake Boot 1");

    public PartitionIdentity Boot2Identity => ToIdentity(Boot2, "VHD integration test fake Boot 2");

    public IReadOnlyList<LivePartition> PreservedPartitions =>
    [
        Efi,
        Msr,
        Boot2,
        Boot1Recovery,
        Boot2Recovery
    ];

    public static DisposableVhdSession Create()
    {
        if (!IsAdministrator())
        {
            throw new InvalidOperationException(
                "The VHD integration test requires an elevated process to create, attach, and delete a VHDX.");
        }

        var session = new DisposableVhdSession(
            Path.Combine(Path.GetTempPath(), $"cleanswitch-vhd-{Guid.NewGuid():N}.vhdx"));
        try
        {
            session.CreateAndAttach();
            session.Proof = session.WaitForProof();
            session.DiskNumber = session.Proof.DiskNumber;
            session.BringDiskOnline();
            session.InitializeLayout();
            session.AssignClassifiedPartitions();
            session.DiskGptId = session.Boot1.DiskGptId
                ?? throw new InvalidOperationException("VHD GPT disk unique id is missing. Refusing.");
            session.RejectProductionPins();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public VirtualDiskProof Reprove() =>
        VirtualDiskProofVerifier.Prove(VhdxPath, DiskNumber);

    public GptLayoutSnapshot CaptureLayout() =>
        new SingleDiskGptLayoutSource(DiskNumber).Capture();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_attached || File.Exists(VhdxPath))
            {
                TryDetach();
            }
        }
        finally
        {
            TryDeleteFile();
        }
    }

    private void CreateAndAttach()
    {
        DiskpartScriptRunner.Run(
        [
            $"create vdisk file=\"{VhdxPath}\" maximum=4096 type=expandable",
            $"select vdisk file=\"{VhdxPath}\"",
            "attach vdisk"
        ]);
        _attached = true;
    }

    private VirtualDiskProof WaitForProof()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return VirtualDiskProofVerifier.Prove(VhdxPath);
            }
            catch (Exception exception)
            {
                if (exception.Message.Contains("disk 0", StringComparison.Ordinal))
                {
                    throw;
                }

                last = exception;
                Thread.Sleep(250);
            }
        }

        throw new InvalidOperationException(
            "Timed out waiting for the temporary VHDX to appear as a virtual PhysicalDrive. " +
            last?.Message);
    }

    private void BringDiskOnline()
    {
        RefreshProof();
        DiskpartScriptRunner.Run(
        [
            $"select disk {DiskNumber}",
            "online disk"
        ]);
        RefreshProof();
        DiskpartScriptRunner.Run(
        [
            $"select disk {DiskNumber}",
            "attributes disk clear readonly"
        ]);
    }

    private void InitializeLayout()
    {
        RefreshProof();
        DiskpartScriptRunner.Run(
        [
            $"select disk {DiskNumber}",
            "convert gpt",
            "create partition efi size=100"
        ]);

        RefreshProof();
        var afterConvert = VolumeLocator.ReadGptTable(DiskNumber);
        if (afterConvert.Count(row => row.GptPartitionType == GptPartitionTypes.MicrosoftReserved) == 0)
        {
            DiskpartScriptRunner.Run(
            [
                $"select disk {DiskNumber}",
                "create partition msr size=16"
            ]);
        }

        RefreshProof();
        DiskpartScriptRunner.Run(
        [
            $"select disk {DiskNumber}",
            "create partition primary size=1024",
            "create partition primary size=300",
            "set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac override",
            "create partition primary size=1024",
            "create partition primary size=300",
            "set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac override"
        ]);
    }

    private void RefreshProof()
    {
        Proof = VirtualDiskProofVerifier.Prove(VhdxPath, DiskNumber == 0 ? null : DiskNumber);
        DiskNumber = Proof.DiskNumber;
    }

    private void AssignClassifiedPartitions()
    {
        var rows = VolumeLocator.ReadGptTable(DiskNumber);
        if (rows.Count < 6)
        {
            throw new InvalidOperationException(
                $"VHD GPT table has {rows.Count} partitions; expected at least 6. Refusing.");
        }

        var data = rows
            .Where(row => row.GptPartitionType == GptPartitionTypes.BasicData)
            .OrderBy(row => row.StartingOffset)
            .ToList();
        var recovery = rows
            .Where(row => row.GptPartitionType == GptPartitionTypes.MicrosoftRecovery)
            .OrderBy(row => row.StartingOffset)
            .ToList();

        if (data.Count != 2)
        {
            throw new InvalidOperationException(
                $"VHD has {data.Count} Basic Data partitions; expected 2 (fake Boot 1 and Boot 2). Refusing.");
        }

        if (recovery.Count != 2)
        {
            throw new InvalidOperationException(
                $"VHD has {recovery.Count} Recovery partitions; expected 2. Refusing.");
        }

        Efi = ToLive(RequireSingle(rows, GptPartitionTypes.EfiSystem, "EFI"));
        Msr = ToLive(RequireSingle(rows, GptPartitionTypes.MicrosoftReserved, "MSR"));
        Boot1 = ToLive(data[0]);
        Boot1Recovery = ToLive(recovery[0]);
        Boot2 = ToLive(data[1]);
        Boot2Recovery = ToLive(recovery[1]);
    }

    private static LocatedGptPartition RequireSingle(
        IReadOnlyList<LocatedGptPartition> rows,
        Guid type,
        string name)
    {
        var matches = rows.Where(row => row.GptPartitionType == type).ToList();
        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"VHD has {matches.Count} {name} partitions; expected 1. Refusing.");
        }

        return matches[0];
    }

    private static LivePartition ToLive(LocatedGptPartition row) =>
        new()
        {
            PartitionGptId = row.GptPartitionId,
            DiskGptId = row.DiskGptUniqueId,
            DiskNumber = row.DiskNumber,
            PartitionNumber = row.PartitionNumber,
            PartitionType = row.GptPartitionType,
            StartingOffset = row.StartingOffset,
            SizeBytes = row.SizeBytes
        };

    private void RejectProductionPins()
    {
        Guid[] live =
        [
            Efi.PartitionGptId,
            Msr.PartitionGptId,
            Boot1.PartitionGptId,
            Boot1Recovery.PartitionGptId,
            Boot2.PartitionGptId,
            Boot2Recovery.PartitionGptId
        ];

        Guid[] production =
        [
            PinnedRetirementTargets.Boot1GptId,
            PinnedRetirementTargets.Boot2GptId,
            Guid.Parse(PinnedRetirementTargets.EfiGpt),
            Guid.Parse(PinnedRetirementTargets.Boot1WinReGpt),
            Guid.Parse(PinnedRetirementTargets.Boot2WinReGpt)
        ];

        if (live.Intersect(production).Any())
        {
            throw new InvalidOperationException(
                "A VHD GPT unique id collided with a production PC pin. Refusing rather than using those pins.");
        }

        if (Boot1.DiskNumber == 0)
        {
            throw new InvalidOperationException("Classified VHD Boot 1 is on disk 0. Refusing.");
        }
    }

    private void TryDetach()
    {
        try
        {
            DiskpartScriptRunner.Run(
            [
                $"select vdisk file=\"{VhdxPath}\"",
                "detach vdisk"
            ]);
        }
        catch (Exception)
        {
        }
        finally
        {
            _attached = false;
        }
    }

    private void TryDeleteFile()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(VhdxPath))
                {
                    File.Delete(VhdxPath);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static PartitionIdentity ToIdentity(LivePartition part, string source) =>
        new()
        {
            DiskNumber = part.DiskNumber,
            PartitionNumber = part.PartitionNumber,
            GptPartitionId = VolumeLocator.FormatGptId(part.PartitionGptId),
            DiskGptUniqueId = part.DiskGptId is null ? null : VolumeLocator.FormatGptId(part.DiskGptId.Value),
            PartitionStartingOffset = part.StartingOffset,
            PartitionSizeBytes = part.SizeBytes,
            GptPartitionType = part.PartitionType is null
                ? null
                : VolumeLocator.FormatGptId(part.PartitionType.Value),
            Source = source
        };

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
