namespace CleanSwitch.Recovery;

public static class WinReDeploymentHashPolicy
{
    public static string RequireSha256(string? value, string name)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (normalized is null || normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidOperationException($"{name} must be an explicit 64-character SHA256 value.");
        return normalized;
    }

    public static void RequireExpectedMatchesObserved(string? expected, string? observed, string boundary)
    {
        var normalizedExpected = RequireSha256(expected, "Expected original Winre.wim SHA256");
        var normalizedObserved = RequireSha256(observed, "Observed original Winre.wim SHA256");
        if (!string.Equals(normalizedExpected, normalizedObserved, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{boundary}: expected original Winre.wim SHA256 {normalizedExpected}, observed {normalizedObserved}. No live mutation is permitted.");
    }

    public static void RequireSealedPlan(WinReDeploymentPlan plan)
    {
        if (plan.SchemaVersion != WinReDeploymentPlan.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"WinRE deployment plan schema {plan.SchemaVersion} is not current schema {WinReDeploymentPlan.CurrentSchemaVersion}.");
        RequireExpectedMatchesObserved(
            plan.ExpectedOriginalWimSha256,
            plan.ObservedOriginalWimSha256,
            "Sealed deployment plan");
        if (!string.Equals(
                RequireSha256(plan.ObservedOriginalWimSha256, "Observed original Winre.wim SHA256"),
                RequireSha256(plan.OriginalWimSha256, "Compatibility original Winre.wim SHA256"),
                StringComparison.Ordinal))
            throw new InvalidOperationException("Sealed deployment plan contains inconsistent observed original hashes.");
    }
}
