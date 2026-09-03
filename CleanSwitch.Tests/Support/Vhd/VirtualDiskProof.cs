using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support.Vhd;

internal sealed record VirtualDiskProof(
    string VhdxPath,
    int DiskNumber,
    string PhysicalDrivePath,
    StorageBusType BusType,
    bool HostsRunningSystemVolume)
{
    public string Describe() =>
        $"vhdx={VhdxPath} disk={DiskNumber} physical={PhysicalDrivePath} " +
        $"bus={BusType} hostsRunningSystem={HostsRunningSystemVolume}";
}

/// <summary>
/// Independent proofs that a disk number is the temporary VHDX, not the NVMe.
/// Fail closed on any missing or contradictory signal.
/// </summary>
internal static class VirtualDiskProofVerifier
{
    public static VirtualDiskProof Prove(
        string vhdxPath,
        int? expectedDiskNumber = null,
        Action<string>? diagnostic = null,
        VirtDiskNative.VirtualDiskAttachment? attachment = null)
    {
        if (string.IsNullOrWhiteSpace(vhdxPath) || !File.Exists(vhdxPath))
        {
            throw new InvalidOperationException(
                "Temporary VHDX file is missing. Refusing to treat any disk as the test target.");
        }

        var fullPath = Path.GetFullPath(vhdxPath);
        diagnostic?.Invoke(
            $"Proving exact VHDX mapping path='{fullPath}' expectedDisk=" +
            $"{(expectedDiskNumber?.ToString() ?? "(discover)")} pid={Environment.ProcessId}.");
        if (attachment is not null && !string.Equals(attachment.Path, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Retained VHD attachment path '{attachment.Path}' does not match proof path '{fullPath}'. Refusing.");
        }

        var physical = attachment?.GetPhysicalDrivePath()
            ?? VirtDiskNative.GetPhysicalDrivePath(fullPath, diagnostic);
        var diskNumber = VirtDiskNative.ParsePhysicalDriveNumber(physical);

        if (diskNumber == 0)
        {
            throw new InvalidOperationException(
                "The attached VHDX resolved to disk 0. Refusing: disk 0 is reserved as the physical system disk.");
        }

        if (expectedDiskNumber is int expected && expected != diskNumber)
        {
            throw new InvalidOperationException(
                $"VHDX '{fullPath}' is PhysicalDrive{diskNumber}, not the expected disk {expected}. Refusing.");
        }

        var busType = StorageBusProbe.ReadBusType(diskNumber);
        if (!StorageBusProbe.IsVirtualBus(busType))
        {
            throw new InvalidOperationException(
                $"Disk {diskNumber} bus type is {busType}, not Virtual/FileBackedVirtual. " +
                "Refusing: this is not a proven virtual disk.");
        }

        var hostsSystem = HostsRunningSystem(diskNumber);
        if (hostsSystem)
        {
            throw new InvalidOperationException(
                $"Disk {diskNumber} hosts the running system volume. Refusing.");
        }

        var proof = new VirtualDiskProof(fullPath, diskNumber, physical, busType, hostsSystem);
        diagnostic?.Invoke("VHDX mapping proof passed: " + proof.Describe());
        return proof;
    }

    public static void ProveResolvedTarget(
        VirtualDiskProof expected,
        ResolvedDeletionTarget target,
        Guid expectedBoot1Gpt,
        Guid expectedDiskGpt,
        IReadOnlyCollection<Guid> protectedGpts,
        VirtDiskNative.VirtualDiskAttachment attachment)
    {
        var live = Prove(expected.VhdxPath, expected.DiskNumber, attachment: attachment);

        if (target.DiskNumber == 0)
        {
            throw new InvalidOperationException("Resolved target is disk 0. Refusing diskpart.");
        }

        if (target.DiskNumber != live.DiskNumber || target.DiskNumber != expected.DiskNumber)
        {
            throw new InvalidOperationException(
                $"Resolved disk {target.DiskNumber} is not the proven VHDX disk {live.DiskNumber}. Refusing diskpart.");
        }

        if (target.DiskGptId is not Guid diskGpt || diskGpt != expectedDiskGpt)
        {
            throw new InvalidOperationException(
                "Resolved disk GPT unique id does not match the VHDX disk GPT id. Refusing diskpart.");
        }

        if (target.TargetGptId != expectedBoot1Gpt)
        {
            throw new InvalidOperationException(
                "Resolved target GPT unique id is not the fake Boot 1 recorded from this VHDX. Refusing diskpart.");
        }

        if (protectedGpts.Contains(target.TargetGptId))
        {
            throw new InvalidOperationException(
                "Resolved target GPT unique id is a protected partition. Refusing diskpart.");
        }

        if (target.PartitionType != GptPartitionTypes.BasicData)
        {
            throw new InvalidOperationException(
                $"Resolved target GPT type is {GptPartitionTypes.Describe(target.PartitionType)}, not Basic Data. Refusing.");
        }
    }

    private static bool HostsRunningSystem(int diskNumber)
    {
        var located = VolumeLocator.Enumerate();
        return located.Volumes.Any(volume =>
            volume.IsRunningSystemVolume && volume.DiskNumber == diskNumber);
    }
}
