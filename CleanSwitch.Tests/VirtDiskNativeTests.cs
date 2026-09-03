using CleanSwitch.Tests.Support.Vhd;

namespace CleanSwitch.Tests;

public sealed class VirtDiskNativeTests
{
    [Fact]
    public void Open_retries_only_sharing_violation_then_succeeds()
    {
        var attempts = 0;
        var delays = new List<int>();

        var handle = VirtDiskNative.OpenWithBoundedRetry(
            () => ++attempts < 3
                ? new(VirtDiskNative.ErrorSharingViolation, IntPtr.Zero)
                : new(0, new IntPtr(1234)),
            delays.Add,
            diagnostic: null,
            vhdxPath: @"C:\temp\bounded-retry.vhdx");

        Assert.Equal(new IntPtr(1234), handle);
        Assert.Equal(3, attempts);
        Assert.Equal(
            [VirtDiskNative.SharingViolationDelayMilliseconds,
             VirtDiskNative.SharingViolationDelayMilliseconds],
            delays);
    }

    [Fact]
    public void Persistent_sharing_violation_fails_closed_after_fixed_attempt_count()
    {
        var attempts = 0;
        var delays = new List<int>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            VirtDiskNative.OpenWithBoundedRetry(
                () =>
                {
                    attempts++;
                    return new(VirtDiskNative.ErrorSharingViolation, IntPtr.Zero);
                },
                delays.Add,
                diagnostic: null,
                vhdxPath: @"C:\temp\persistent-lock.vhdx"));

        Assert.Equal(VirtDiskNative.OpenAttemptLimit, attempts);
        Assert.Equal(VirtDiskNative.OpenAttemptLimit - 1, delays.Count);
        Assert.Contains("status=32", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mapping is unproven", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_sharing_error_is_not_retried()
    {
        var attempts = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            VirtDiskNative.OpenWithBoundedRetry(
                () =>
                {
                    attempts++;
                    return new(5, IntPtr.Zero);
                },
                _ => throw new InvalidOperationException("Delay must not be called."),
                diagnostic: null,
                vhdxPath: @"C:\temp\access-denied.vhdx"));

        Assert.Equal(1, attempts);
        Assert.Contains("status=5", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mapping is unproven", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exclusive_creation_handle_causes_fail_closed_sharing_violation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cleanswitch-open-handle-{Guid.NewGuid():N}.vhdx");
        try
        {
            using var creationHandle = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                VirtDiskNative.GetPhysicalDrivePath(path));

            Assert.Contains("status=32", exception.Message, StringComparison.Ordinal);
            Assert.Contains("mapping is unproven", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\.\PhysicalDrive")]
    [InlineData(@"\\.\PhysicalDriveNotANumber")]
    [InlineData(@"C:\temp\disk.vhdx")]
    public void Unproven_physical_drive_mapping_is_rejected(string mapping)
    {
        Assert.Throws<InvalidOperationException>(() =>
            VirtDiskNative.ParsePhysicalDriveNumber(mapping));
    }
}