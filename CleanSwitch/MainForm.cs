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
    }

    private async Task DetectCurrentWindowsAsync()
    {
        switchButton.Enabled = false;
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

            // FUTURE: After Boot 2 is running, a later phase may wipe Boot 1.
            // This click must not format or delete any partition.
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

    private void ShowDetectError(string message)
    {
        _layout = null;
        currentValueLabel.Text = "Unknown";
        targetValueLabel.Text = "Unknown";
        switchButton.Enabled = false;
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
        MessageBox.Show(
            this,
            message,
            "CleanSwitch",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
