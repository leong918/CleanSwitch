using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CleanSwitch.Tests.Support.Vhd;

/// <summary>
/// Inbox virtdisk.dll lifecycle for a disposable VHDX. A dedicated handle owns the
/// attachment for the entire test. Independent GET_INFO handles prove that the exact
/// backing path still resolves to the expected PhysicalDrive.
/// </summary>
internal static class VirtDiskNative
{
    private static readonly Guid VendorMicrosoft = Guid.Parse("EC984AEC-A0F9-47e9-901F-71415A66345B");

    private const int VirtualStorageTypeDeviceVhdx = 3;
    private const int VirtualDiskAccessAttachReadWrite = 0x00020000;
    private const int VirtualDiskAccessDetach = 0x00040000;
    internal const int VirtualDiskAccessGetInfo = 0x00080000;
    internal const int VirtualDiskAttachmentAccess =
        VirtualDiskAccessAttachReadWrite | VirtualDiskAccessDetach | VirtualDiskAccessGetInfo;
    private const int OpenVirtualDiskVersion1 = 1;
    private const int OpenVirtualDiskFlagNone = 0;
    private const int AttachVirtualDiskFlagNoDriveLetter = 0x00000002;
    private const int AttachVirtualDiskFlagNoSecurityDescriptor = 0x00000010;
    private const int AttachVirtualDiskFlags =
        AttachVirtualDiskFlagNoDriveLetter | AttachVirtualDiskFlagNoSecurityDescriptor;
    private const int AttachVirtualDiskVersion1 = 1;
    private const int DetachVirtualDiskFlagNone = 0;
    internal const int ErrorSharingViolation = 32;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorSuccess = 0;
    internal const int OpenAttemptLimit = 4;
    internal const int SharingViolationDelayMilliseconds = 100;

    public static VirtualDiskAttachment Attach(string vhdxPath, Action<string>? diagnostic = null)
    {
        var fullPath = Path.GetFullPath(vhdxPath);
        diagnostic?.Invoke(
            $"OpenVirtualDisk attachment begin path='{fullPath}' pid={Environment.ProcessId} " +
            $"access=ATTACH_RW|DETACH|GET_INFO(0x{VirtualDiskAttachmentAccess:X8}) " +
            $"openFlags=NONE(0x{OpenVirtualDiskFlagNone:X8}) " +
            $"attachFlags=NO_DRIVE_LETTER|NO_SECURITY_DESCRIPTOR(0x{AttachVirtualDiskFlags:X8}).");

        var handle = OpenWithBoundedRetry(
            () => Open(fullPath, VirtualDiskAttachmentAccess),
            Thread.Sleep,
            diagnostic,
            fullPath,
            VirtualDiskAttachmentAccess);

        var attachParameters = new AttachVirtualDiskParameters
        {
            Version = AttachVirtualDiskVersion1,
            Reserved = 0
        };
        EnableManageVolumePrivilege(diagnostic);
        var status = AttachVirtualDisk(
            handle,
            IntPtr.Zero,
            AttachVirtualDiskFlags,
            0,
            ref attachParameters,
            IntPtr.Zero);
        if (status != ErrorSuccess)
        {
            CloseHandle(handle);
            throw Failure("AttachVirtualDisk", fullPath, status, AttachVirtualDiskFlags);
        }

        diagnostic?.Invoke(
            $"AttachVirtualDisk succeeded path='{fullPath}' handle=0x{handle.ToInt64():X}; " +
            "handle retained for the complete test lifecycle.");
        return new VirtualDiskAttachment(fullPath, handle, diagnostic);
    }

    public static string GetPhysicalDrivePath(string vhdxPath, Action<string>? diagnostic = null)
    {
        var fullPath = Path.GetFullPath(vhdxPath);
        diagnostic?.Invoke(
            $"OpenVirtualDisk proof begin path='{fullPath}' pid={Environment.ProcessId} " +
            $"access=GET_INFO(0x{VirtualDiskAccessGetInfo:X8}) flags=NONE(0x{OpenVirtualDiskFlagNone:X8}).");

        var handle = OpenWithBoundedRetry(
            () => Open(fullPath, VirtualDiskAccessGetInfo),
            Thread.Sleep,
            diagnostic,
            fullPath,
            VirtualDiskAccessGetInfo);
        try
        {
            return GetPhysicalDrivePath(handle, fullPath, diagnostic);
        }
        finally
        {
            var closed = CloseHandle(handle);
            diagnostic?.Invoke(
                $"OpenVirtualDisk proof handle closed path='{fullPath}' " +
                $"handle=0x{handle.ToInt64():X} success={closed}.");
        }
    }

    internal static IntPtr OpenWithBoundedRetry(
        Func<VirtualDiskOpenAttempt> open,
        Action<int> delay,
        Action<string>? diagnostic,
        string vhdxPath,
        int accessMask = VirtualDiskAccessGetInfo)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(delay);

        for (var attemptNumber = 1; attemptNumber <= OpenAttemptLimit; attemptNumber++)
        {
            var result = open();
            var errorText = ErrorText(result.Status);
            diagnostic?.Invoke(
                $"OpenVirtualDisk attempt={attemptNumber}/{OpenAttemptLimit} status={result.Status} " +
                $"error='{errorText}' access=0x{accessMask:X8} handle=0x{result.Handle.ToInt64():X}.");

            if (result.Status == ErrorSuccess && result.Handle != IntPtr.Zero)
            {
                return result.Handle;
            }

            if (result.Handle != IntPtr.Zero)
            {
                CloseHandle(result.Handle);
            }

            if (result.Status != ErrorSharingViolation || attemptNumber == OpenAttemptLimit)
            {
                throw new InvalidOperationException(
                    $"OpenVirtualDisk failed for '{vhdxPath}' " +
                    $"(status={result.Status}, error='{errorText}', access=0x{accessMask:X8}, " +
                    $"attempt={attemptNumber}/{OpenAttemptLimit}). " +
                    "The exact VHDX-to-PhysicalDrive mapping is unproven. Refusing.");
            }

            diagnostic?.Invoke(
                $"OpenVirtualDisk sharing violation; retrying after " +
                $"{SharingViolationDelayMilliseconds} ms.");
            delay(SharingViolationDelayMilliseconds);
        }

        throw new InvalidOperationException("OpenVirtualDisk retry loop terminated unexpectedly. Refusing.");
    }

    public static int ParsePhysicalDriveNumber(string physicalDrivePath)
    {
        const string prefix = @"\\.\PhysicalDrive";
        if (!physicalDrivePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $@"Physical drive path '{physicalDrivePath}' is not a \\.\PhysicalDriveN path. Refusing.");
        }

        if (!int.TryParse(physicalDrivePath[prefix.Length..], out var diskNumber))
        {
            throw new InvalidOperationException(
                $"Could not parse disk number from '{physicalDrivePath}'. Refusing.");
        }

        return diskNumber;
    }

    private static VirtualDiskOpenAttempt Open(string path, int accessMask)
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
            path,
            accessMask,
            OpenVirtualDiskFlagNone,
            ref parameters,
            out var handle);
        return new VirtualDiskOpenAttempt(status, handle);
    }

    private static string GetPhysicalDrivePath(
        IntPtr handle,
        string vhdxPath,
        Action<string>? diagnostic)
    {
        var bytes = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder(bytes / 2);
            var size = bytes;
            var status = GetVirtualDiskPhysicalPath(handle, ref size, buffer);
            if (status == ErrorSuccess)
            {
                var path = buffer.ToString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException(
                        "GetVirtualDiskPhysicalPath returned an empty path. Refusing.");
                }

                diagnostic?.Invoke(
                    $"GetVirtualDiskPhysicalPath resolved path='{vhdxPath}' to '{path}'.");
                return path;
            }

            if (status != ErrorInsufficientBuffer)
            {
                throw Failure("GetVirtualDiskPhysicalPath", vhdxPath, status, flags: 0);
            }

            bytes = Math.Max(size + 2, bytes * 2);
        }

        throw new InvalidOperationException("GetVirtualDiskPhysicalPath kept reporting an insufficient buffer.");
    }

    private static void EnableManageVolumePrivilege(Action<string>? diagnostic)
    {
        const string privilegeName = "SeManageVolumePrivilege";
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"OpenProcessToken failed enabling {privilegeName}: status={error}, error='{ErrorText(error)}'. Refusing attach.");
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"LookupPrivilegeValue failed for {privilegeName}: status={error}, error='{ErrorText(error)}'. Refusing attach.");
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"AdjustTokenPrivileges failed for {privilegeName}: status={error}, error='{ErrorText(error)}'. Refusing attach.");
            }

            var adjustmentError = Marshal.GetLastWin32Error();
            if (adjustmentError != 0)
            {
                throw new InvalidOperationException(
                    $"AdjustTokenPrivileges did not enable {privilegeName}: status={adjustmentError}, " +
                    $"error='{ErrorText(adjustmentError)}'. Refusing attach.");
            }

            diagnostic?.Invoke($"Enabled {privilegeName} for pid={Environment.ProcessId} before AttachVirtualDisk.");
        }
        finally
        {
            CloseHandle(token);
            diagnostic?.Invoke("Privilege token handle closed.");
        }
    }
    private static InvalidOperationException Failure(string api, string path, int status, int flags) =>
        new(
            $"{api} failed for '{path}' " +
            $"(status={status}, error='{ErrorText(status)}', flags=0x{flags:X8}). Refusing.");

    private static string ErrorText(int status) => status == ErrorSuccess
        ? "The operation completed successfully."
        : new Win32Exception(status).Message;

    internal sealed class VirtualDiskAttachment : IDisposable
    {
        private readonly Action<string>? _diagnostic;
        private IntPtr _handle;

        internal VirtualDiskAttachment(string path, IntPtr handle, Action<string>? diagnostic)
        {
            Path = path;
            _handle = handle;
            _diagnostic = diagnostic;
        }

        public string Path { get; }

        public string GetPhysicalDrivePath()
        {
            if (_handle == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(VirtualDiskAttachment));
            }

            _diagnostic?.Invoke($"Resolving PhysicalDrive through retained path-bound handle path='{Path}' handle=0x{_handle.ToInt64():X}.");
            return VirtDiskNative.GetPhysicalDrivePath(_handle, Path, _diagnostic);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            var handle = _handle;
            _handle = IntPtr.Zero;
            try
            {
                var status = DetachVirtualDisk(handle, DetachVirtualDiskFlagNone, 0);
                _diagnostic?.Invoke(
                    $"DetachVirtualDisk path='{Path}' handle=0x{handle.ToInt64():X} " +
                    $"flags=NONE status={status} error='{ErrorText(status)}'.");
                if (status != ErrorSuccess)
                {
                    throw Failure("DetachVirtualDisk", Path, status, DetachVirtualDiskFlagNone);
                }
            }
            finally
            {
                var closed = CloseHandle(handle);
                _diagnostic?.Invoke(
                    $"Attachment handle closed path='{Path}' handle=0x{handle.ToInt64():X} success={closed}.");
            }
        }
    }

    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const int SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AttachVirtualDiskParameters
    {
        public int Version;
        public int Reserved;
    }

    internal readonly record struct VirtualDiskOpenAttempt(int Status, IntPtr Handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        int virtualDiskAccessMask,
        int flags,
        ref OpenVirtualDiskParameters parameters,
        out IntPtr handle);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    private static extern int AttachVirtualDisk(
        IntPtr virtualDiskHandle,
        IntPtr securityDescriptor,
        int flags,
        int providerSpecificFlags,
        ref AttachVirtualDiskParameters parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    private static extern int DetachVirtualDisk(
        IntPtr virtualDiskHandle,
        int flags,
        int providerSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetVirtualDiskPhysicalPath(
        IntPtr virtualDiskHandle,
        ref int diskPathSizeInBytes,
        StringBuilder diskPath);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}