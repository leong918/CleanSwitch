using CleanSwitch.Recovery;

namespace CleanSwitch.Tests.Support;

internal sealed class FakeWinReLauncherValidator : IWinReLauncherValidator
{
    private readonly IList<string>? _events;

    public FakeWinReLauncherValidator(bool passed = true, IList<string>? events = null)
    {
        Passed = passed;
        _events = events;
    }

    public bool Passed { get; set; }
    public string FailureDetail { get; set; } = "fixture launcher is not provisioned";
    public int CallCount { get; private set; }

    public Task<WinReLauncherValidationResult> ValidateAsync(
        RecoveryEntryResolution recovery,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        _events?.Add("launcher");
        var report = new ValidationReport("fixture WinRE launcher");
        if (Passed)
        {
            report.Pass("launcher", "fixture launcher matches");
        }
        else
        {
            report.Fail("launcher", FailureDetail);
        }

        return Task.FromResult(new WinReLauncherValidationResult(report));
    }
}
