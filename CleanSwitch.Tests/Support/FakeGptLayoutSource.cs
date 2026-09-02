using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support;

internal sealed class FakeGptLayoutSource : IGptLayoutSource
{
    public FakeGptLayoutSource(GptLayoutSnapshot current)
    {
        Current = current;
    }

    public GptLayoutSnapshot Current { get; set; }

    public Exception? ThrowOnCapture { get; set; }

    public int CaptureCount { get; private set; }

    public GptLayoutSnapshot Capture()
    {
        CaptureCount++;
        if (ThrowOnCapture is not null)
        {
            throw ThrowOnCapture;
        }

        return Current;
    }
}
