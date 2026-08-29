namespace CleanSwitch.Recovery;

/// <summary>One recorded validation decision.</summary>
public sealed record ValidationCheck(string Name, bool Passed, string Detail)
{
    public override string ToString() => $"[{(Passed ? "PASS" : "FAIL")}] {Name}: {Detail}";
}

/// <summary>
/// Result of a validator. Every individual decision is retained so the log explains why
/// an operation was allowed or refused, not just that it was.
/// </summary>
public sealed class ValidationReport
{
    private readonly List<ValidationCheck> _checks = [];

    public ValidationReport(string subject)
    {
        Subject = subject;
    }

    public string Subject { get; }

    public IReadOnlyList<ValidationCheck> Checks => _checks;

    public bool Passed => _checks.Count > 0 && _checks.All(check => check.Passed);

    public ValidationReport Add(string name, bool passed, string detail)
    {
        _checks.Add(new ValidationCheck(name, passed, detail));
        return this;
    }

    public ValidationReport Pass(string name, string detail) => Add(name, true, detail);

    public ValidationReport Fail(string name, string detail) => Add(name, false, detail);

    public string FirstFailure =>
        _checks.FirstOrDefault(check => !check.Passed)?.Detail ?? string.Empty;

    public string Describe() =>
        $"{Subject}: {(Passed ? "PASSED" : "FAILED")}" +
        Environment.NewLine +
        string.Join(Environment.NewLine, _checks.Select(check => "  " + check));
}
