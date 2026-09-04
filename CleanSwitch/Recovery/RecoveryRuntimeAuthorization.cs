using Microsoft.Win32;

namespace CleanSwitch.Recovery;

public sealed record RecoveryRuntimeEvidence(bool IsWindowsPe, BcdAliasResolution CurrentResolution, Guid? CurrentBcdObjectId);

public interface IRecoveryRuntimeProof
{
    Task<RecoveryRuntimeEvidence> CaptureAsync();
}

public sealed class WindowsRecoveryRuntimeProof(IBcdStoreSource bcdStore) : IRecoveryRuntimeProof
{
    public async Task<RecoveryRuntimeEvidence> CaptureAsync()
    {
        using var miniNt = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT", writable: false);
        var snapshot = await bcdStore.CaptureAsync();
        return new RecoveryRuntimeEvidence(miniNt is not null, snapshot.CurrentResolution, snapshot.CurrentObjectId);
    }
}
