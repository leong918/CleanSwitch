using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CleanSwitch.Tests.Support.Vhd;

internal enum StorageBusType : byte
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    RAID = 8,
    iScsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
    SCM = 18,
    Ufs = 19
}

/// <summary>Query-only IOCTL_STORAGE_QUERY_PROPERTY. Never writes to the disk.</summary>
internal static class StorageBusProbe
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int StorageDeviceProperty = 0;
    private const int PropertyStandardQuery = 0;
    private const int BusTypeOffset = 28;

    public static StorageBusType ReadBusType(int diskNumber)
    {
        var devicePath = $@"\\.\PHYSICALDRIVE{diskNumber}";
        using var handle = CreateFileW(
            devicePath,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new InvalidOperationException(
                $"Could not open '{devicePath}' to read bus type (Win32 {Marshal.GetLastWin32Error()}). Refusing.");
        }

        var query = new byte[12];
        BitConverter.GetBytes(StorageDeviceProperty).CopyTo(query, 0);
        BitConverter.GetBytes(PropertyStandardQuery).CopyTo(query, 4);

        var descriptor = new byte[256];
        if (!DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                query,
                (uint)query.Length,
                descriptor,
                (uint)descriptor.Length,
                out var returned,
                IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"IOCTL_STORAGE_QUERY_PROPERTY failed for disk {diskNumber} " +
                $"(Win32 {Marshal.GetLastWin32Error()}). Refusing.");
        }

        if (returned < BusTypeOffset + 1)
        {
            throw new InvalidOperationException(
                $"STORAGE_DEVICE_DESCRIPTOR for disk {diskNumber} was too small. Refusing.");
        }

        return (StorageBusType)descriptor[BusTypeOffset];
    }

    public static bool IsVirtualBus(StorageBusType busType) =>
        busType is StorageBusType.Virtual or StorageBusType.FileBackedVirtual;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
