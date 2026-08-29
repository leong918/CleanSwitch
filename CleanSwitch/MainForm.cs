using System.Text.Json;
using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch;

public partial class MainForm : Form
{
    private readonly IBootManager _bootManager = new WindowsBootManager();
    private BootLayout? _layout;
    private int _restartDelaySeconds = 5;

    public MainForm()
    {
        InitializeComponent();
    }

    private async void MainForm_Load(object? sender, EventArgs e)
    {
        await DetectCurrentWindowsAsync();
        ReportPendingRetirement();
    }

    private async Task DetectCurrentWindowsAsync()
    {
        switchButton.Enabled = false;
        retireButton.Enabled = false;
        currentValueLabel.Text = "Detecting...";
        targetValueLabel.Text = "Detecting...";
        switchButton.Text = "Switch boot";
        statusLabel.ForeColor = Color.FromArgb(70, 70, 70);
        statusLabel.Text = "Detecting the currently running Windows...";
        Refresh();

        try
        {
            var options = AppConfiguration.Load();
            _restartDelaySeconds = options.RestartDelaySeconds;
            _layout = await _bootManager.DetectAsync(options.Boot2Guid);

            currentValueLabel.Text = _layout.Current.Description;
            targetValueLabel.Text = _layout.Target.Description;
            switchButton.Text = $"Switch to {_layout.Target.Description}";
            statusLabel.Text = "Ready.";
            switchButton.Enabled = true;
            retireButton.Enabled = true;
        }
        catch (Exception exception) when (exception is BootManagerException or InvalidOperationException or JsonException)
        {
            ShowDetectError(exception.Message);
        }
        catch (Exception exception)
        {
            ShowDetectError($"Could not detect the current Windows boot entry.{Environment.NewLine}{exception.Message}");
        }
    }

    private async void SwitchButton_Click(object? sender, EventArgs e)
    {
        if (_layout is null)
        {
            await DetectCurrentWindowsAsync();
            if (_layout is null)
            {
                return;
            }
        }

        var target = _layout.Target;
        var continueButton = new TaskDialogButton("Continue");
        var page = new TaskDialogPage
        {
            Caption = "CleanSwitch",
            Heading = $"Switch this PC to {target.Description}?",
            Text = "The computer will restart.",
            Buttons = { TaskDialogButton.Cancel, continueButton },
            DefaultButton = TaskDialogButton.Cancel,
            AllowCancel = true
        };

        if (TaskDialog.ShowDialog(this, page) != continueButton)
        {
            return;
        }

        switchButton.Enabled = false;
        retireButton.Enabled = false;
        statusLabel.Text = $"Switching to {target.Description}...";
        statusLabel.ForeColor = Color.FromArgb(70, 70, 70);
        Refresh();

        try
        {
            var bootTargetSet = await _bootManager.SetNextBootAsync(target.Identifier);
            if (!bootTargetSet)
            {
                ShowSwitchError("BCDEdit failed. The computer will not be restarted.");
                return;
            }

            // This click never deletes anything. Retiring Boot 1 is the separate
            // RETIRE SYSTEM action, which runs its work from the recovery environment.
            await _bootManager.RestartAsync(_restartDelaySeconds);
        }
        catch (Exception exception) when (exception is BootManagerException or InvalidOperationException or JsonException)
        {
            ShowSwitchError(exception.Message);
        }
        catch (Exception exception)
        {
            ShowSwitchError($"An unexpected error occurred while switching boot entries.{Environment.NewLine}{exception.Message}");
        }
    }

    /// <summary>
    /// Starts the Boot 1 retirement handoff: record PENDING state, point the next boot at
    /// the recovery environment, restart. Nothing is deleted here or in Phase 2A at all.
    /// Any failure before the restart leaves the PC exactly as it was.
    /// </summary>
    private async void RetireButton_Click(object? sender, EventArgs e)
    {
        if (_layout is null)
        {
            await DetectCurrentWindowsAsync();
            if (_layout is null)
            {
                return;
            }
        }

        var boot1 = _layout.Current;
        var boot2 = _layout.Target;

        var continueButton = new TaskDialogButton("Continue");
        var page = new TaskDialogPage
        {
            Caption = "CleanSwitch",
            Heading = "This will permanently retire Boot 1 and switch this PC to Boot 2.",
            Text =
                $"Boot 1 (retire): {boot1.Description}{Environment.NewLine}" +
                $"Boot 2 (keep): {boot2.Description}{Environment.NewLine}{Environment.NewLine}" +
                "The PC will restart into the recovery environment to complete the handoff.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.Cancel, continueButton },
            DefaultButton = TaskDialogButton.Cancel,
            AllowCancel = true
        };

        if (TaskDialog.ShowDialog(this, page) != continueButton)
        {
            return;
        }

        switchButton.Enabled = false;
        retireButton.Enabled = false;
        statusLabel.ForeColor = Color.FromArgb(70, 70, 70);
        statusLabel.Text = "Preparing the retirement handoff...";
        Refresh();

        RetirementServices services;
        try
        {
            var options = AppConfiguration.Load();
            _restartDelaySeconds = options.RestartDelaySeconds;
            services = RetirementServices.Create(options, "retire");
            services.Coordinator.EnsureStorageReady();
        }
        catch (Exception exception) when (
            exception is RetirementStorageException or InvalidOperationException or JsonException)
        {
            ShowRetireError(exception.Message);
            return;
        }

        RetirementState? state = null;
        try
        {
            var recovery = await services.BootEntryValidator.ResolveRecoveryEntryAsync(services.Options.RecoveryGuid);
            if (recovery.Identifier is null)
            {
                ShowRetireError(
                    "The Windows Recovery Environment boot entry could not be validated, so nothing was " +
                    "changed and the PC will not restart." + Environment.NewLine + Environment.NewLine +
                    recovery.Report.Describe() + Environment.NewLine + Environment.NewLine +
                    "Find the recovery identifier with 'bcdedit /enum all /v' from an elevated prompt and set " +
                    "CleanSwitch:RecoveryGuid in appsettings.json.");
                return;
            }

            statusLabel.Text = "Recording the retirement state...";
            Refresh();

            state = services.Coordinator.BeginRetirement(boot1.Identifier, boot2.Identifier, recovery.Identifier);

            statusLabel.Text = "Setting the recovery environment as the next boot...";
            Refresh();

            await services.BootManager.SetNextBootAsync(recovery.Identifier);

            statusLabel.Text = $"Restarting into the recovery environment in {_restartDelaySeconds} second(s)...";
            Refresh();

            await services.BootManager.RestartAsync(_restartDelaySeconds);
        }
        catch (Exception exception) when (
            exception is BootManagerException or RetirementStateException or RetirementStorageException
                or InvalidOperationException or JsonException)
        {
            RecordRetireFailure(services, state, exception.Message);
            ShowRetireError(exception.Message);
        }
        catch (Exception exception)
        {
            RecordRetireFailure(services, state, exception.Message);
            ShowRetireError(
                $"An unexpected error occurred while preparing the retirement handoff.{Environment.NewLine}{exception.Message}");
        }
    }

    private static void RecordRetireFailure(RetirementServices services, RetirementState? state, string error)
    {
        services.Log.Warn("retire-ui", $"Retirement handoff aborted before restart: {error}");

        if (state is null)
        {
            return;
        }

        try
        {
            services.Coordinator.MarkFailed(state, error);
        }
        catch (Exception exception) when (
            exception is RetirementStorageException or RetirementStateException)
        {
            services.Log.Warn("retire-ui", $"Could not record the failure in the state file: {exception.Message}");
        }
    }

    /// <summary>
    /// Surfaces an in-flight retirement and closes out a finished one. Failures here are
    /// informational only: a bad recovery data path must not block the switch feature.
    /// </summary>
    private void ReportPendingRetirement()
    {
        if (_layout is null)
        {
            return;
        }

        try
        {
            var options = AppConfiguration.Load();
            var services = RetirementServices.Create(options, "startup");
            var state = services.Coordinator.TryCompleteAfterReboot(_layout.Current.Identifier);
            if (state is null)
            {
                return;
            }

            statusLabel.Text = state.Status switch
            {
                RetirementStatus.Complete =>
                    "Ready. A Boot 1 retirement handoff completed on this PC (nothing was deleted).",
                RetirementStatus.Failed =>
                    $"Ready. A previous retirement attempt failed: {state.LastError}",
                RetirementStatus.Aborted => "Ready. A previous retirement attempt was aborted.",
                _ =>
                    $"Ready. A retirement operation is in progress (status {RetirementStatusNames.ToWire(state.Status)})."
            };
        }
        catch (Exception exception) when (
            exception is RetirementStorageException or InvalidOperationException or JsonException
                or RetirementStateException)
        {
            statusLabel.Text =
                "Ready. Retirement is unavailable until CleanSwitch:RecoveryDataPath is configured.";
        }
    }

    private void ShowDetectError(string message)
    {
        _layout = null;
        currentValueLabel.Text = "Unknown";
        targetValueLabel.Text = "Unknown";
        switchButton.Enabled = false;
        retireButton.Enabled = false;
        statusLabel.Text = "Could not detect the current Windows.";
        statusLabel.ForeColor = Color.FromArgb(160, 40, 40);
        MessageBox.Show(
            this,
            message,
            "CleanSwitch",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void ShowSwitchError(string message)
    {
        statusLabel.Text = "Switch did not start. Windows was not restarted.";
        statusLabel.ForeColor = Color.FromArgb(160, 40, 40);
        switchButton.Enabled = _layout is not null;
        retireButton.Enabled = _layout is not null;
        MessageBox.Show(
            this,
            message,
            "CleanSwitch",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void ShowRetireError(string message)
    {
        statusLabel.Text = "Retirement did not start. Nothing was changed and Windows was not restarted.";
        statusLabel.ForeColor = Color.FromArgb(160, 40, 40);
        switchButton.Enabled = _layout is not null;
        retireButton.Enabled = _layout is not null;
        MessageBox.Show(
            this,
            message,
            "CleanSwitch",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
