using CleanSwitch.Recovery;

namespace CleanSwitch;

internal interface IStartupUserInterface
{
    void Initialize();
    void ShowSafetyBlock(string title, string message);
    int RunMainForm();
}

internal sealed class WinFormsStartupUserInterface : IStartupUserInterface
{
    public void Initialize()
    {
        ApplicationConfiguration.Initialize();
    }

    public void ShowSafetyBlock(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public int RunMainForm()
    {
        Application.Run(new MainForm());
        return 0;
    }
}

internal static class StartupFlow
{
    internal const int DeploymentInterlockFailureExitCode = 3;
    internal const string SafetyBlockTitle = "CleanSwitch Safety Block";

    internal static bool IsNormalDesktopStartup(IReadOnlyCollection<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count == 0;
    }

    internal static int RunNormalDesktop(
        Func<WinReDeploymentJournalInventory> inspectDeploymentInterlock,
        IStartupUserInterface userInterface,
        Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(inspectDeploymentInterlock);
        ArgumentNullException.ThrowIfNull(userInterface);
        ArgumentNullException.ThrowIfNull(report);

        userInterface.Initialize();
        var inventory = inspectDeploymentInterlock();
        if (IsBlocked(inventory))
        {
            return HandleBlockedDeploymentInterlock(inventory, true, userInterface, report);
        }

        return userInterface.RunMainForm();
    }

    internal static bool IsBlocked(WinReDeploymentJournalInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return inventory.Invalid.Count > 0 || inventory.Active.Count > 0;
    }

    internal static int HandleBlockedDeploymentInterlock(
        WinReDeploymentJournalInventory inventory,
        bool showModal,
        IStartupUserInterface? userInterface,
        Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(report);
        if (!IsBlocked(inventory))
            throw new InvalidOperationException("A deployment interlock cannot be handled when no blocker exists.");
        if (showModal && userInterface is null)
            throw new ArgumentNullException(nameof(userInterface));

        report("An incomplete or invalid WinRE deployment transaction exists.");
        report("CleanSwitch will not start a new operation or its GUI until the deployment journal becomes terminal.");
        foreach (var item in inventory.Invalid) report("INVALID: " + item);
        foreach (var item in inventory.Active)
            report($"ACTIVE: {item.Path} stage={item.Last.Stage} sequence={item.Last.Sequence}");
        report("Use --winre-deployment-status. An exact AwaitingSmoke transaction may be independently verified with --commit-winre-deployment; other incomplete states require --recover-winre-deployment. No mutation was attempted.");

        if (showModal)
        {
            userInterface!.ShowSafetyBlock(SafetyBlockTitle, BuildSafetyBlockMessage(BuildBlockerText(inventory)));
        }

        return DeploymentInterlockFailureExitCode;
    }

    internal static string BuildBlockerText(WinReDeploymentJournalInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var blockers = inventory.Invalid
            .Select(item => "INVALID: " + item)
            .Concat(inventory.Active.Select(item =>
                $"ACTIVE: {item.Path} stage={item.Last.Stage} sequence={item.Last.Sequence}"));
        return string.Join(Environment.NewLine, blockers);
    }

    private static string BuildSafetyBlockMessage(string blockerText)
    {
        return "CleanSwitch cannot start normal retirement controls." + Environment.NewLine + Environment.NewLine +
               "Blocker:" + Environment.NewLine + blockerText + Environment.NewLine + Environment.NewLine +
               "No disk/BCD/WinRE change has been performed." + Environment.NewLine + Environment.NewLine +
               "Administrator/service action is required before retirement can be enabled.";
    }
}
