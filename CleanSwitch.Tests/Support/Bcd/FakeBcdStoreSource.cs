using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support.Bcd;

internal sealed class FakeBcdStoreSource : IBcdStoreSource
{
    public FakeBcdStoreSource(BcdSnapshot current)
    {
        Current = current;
    }

    public BcdSnapshot Current { get; set; }

    public Exception? ThrowOnCapture { get; set; }

    public Task<BcdSnapshot> CaptureAsync()
    {
        if (ThrowOnCapture is not null)
        {
            throw ThrowOnCapture;
        }

        return Task.FromResult(Current);
    }
}
