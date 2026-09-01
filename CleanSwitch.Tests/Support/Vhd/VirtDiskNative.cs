using System.Runtime.InteropServices;
using System.Text;

namespace CleanSwitch.Tests.Support.Vhd;

/// <summary>
/// Inbox virtdisk.dll: open a VHDX file and ask Windows which PhysicalDrive it is.
/// That file-to-disk mapping is the primary proof the target is our temporary VHDX.
/// </summary>
internal static class VirtDiskNative
{
    private static readonly Guid VendorMicrosoft = Guid.Parse("EC984AEC-A0F9-47e9-901F-71415A66345B");

    private const int VirtualStorageTypeDeviceVhdx = 3;
    private const int VirtualDiskAccessAll = 0x003F0000;
    private const int OpenVirtualDiskVersion1 = 1;
    private const int OpenVirtualDiskFlagNone = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorSuccess = 0;

    public static string GetPhysicalDrivePath(string vhdxPath)
    {
        var storageType = new VirtualStorageType
        {
            DeviceId = VirtualStorageTypeDeviceVhdx,
            VendorId = VendorMicrosoft
        };
        var parameters = new OpenVirtualDiskParameters
        {
            Version = OpenVirtualDiskVersion1,
            RWDepth = 1
        };

        var status = OpenVirtualDisk(
            ref storageType,
            vhdxPath,
            VirtualDiskAccessAll,
            OpenVirtualDiskFlagNone,
            ref parameters,
            out var handle);

        if (status != ErrorSuccess || handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"OpenVirtualDisk failed for '{vhdxPath}' (status={status}). " +
                "The VHDX is not attached or cannot be opened. Refusing.");
        }

        try
        {
            var bytes = 512;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var buffer = new StringBuilder(bytes / 2);
                var size = bytes;
                status = GetVirtualDiskPhysicalPath(handle, ref size, buffer);
                if (status == ErrorSuccess)
                {
                    var path = buffer.ToString();
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        throw new InvalidOperationException(
                            "GetVirtualDiskPhysicalPath returned an empty path. Refusing.");
                    }

                    return path;
                }

                if (status != ErrorInsufficientBuffer)
                {
                    throw new InvalidOperationException(
                        $"GetVirtualDiskPhysicalPath failed for '{vhdxPath}' (status={status}). Refusing.");
                }

                bytes = Math.Max(size + 2, bytes * 2);
            }

            throw new InvalidOperationException("GetVirtualDiskPhysicalPath kept reporting an insufficient buffer.");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static int ParsePhysicalDriveNumber(string physicalDrivePath)
    {
        const string prefix = @"\\.\PhysicalDrive";
        if (!physicalDrivePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Physical drive path '{physicalDrivePath}' is not a \\\\.\\PhysicalDriveN path. Refusing.");
        }

        if (!int.TryParse(physicalDrivePath[prefix.Length..], out var diskNumber))
        {
            throw new InvalidOperationException(
                $"Could not parse disk number from '{physicalDrivePath}'. Refusing.");
        }

        return diskNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public int DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenVirtualDiskParameters
    {
        public int Version;
        public int RWDepth;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        int virtualDiskAccessMask,
        int flags,
        ref OpenVirtualDiskParameters parameters,
        out IntPtr handle);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetVirtualDiskPhysicalPath(
        IntPtr virtualDiskHandle,
        ref int diskPathSizeInBytes,
        StringBuilder diskPath);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
