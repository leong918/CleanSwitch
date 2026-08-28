#nullable enable

namespace CleanSwitch;

partial class MainForm
{
    private System.ComponentModel.IContainer? components = null;
    private Label titleLabel = null!;
    private Label currentHeadingLabel = null!;
    private Label currentValueLabel = null!;
    private Label targetHeadingLabel = null!;
    private Label targetValueLabel = null!;
    private Button switchButton = null!;
    private Label statusLabel = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components is not null)
        {
            components.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        titleLabel = new Label();
        currentHeadingLabel = new Label();
        currentValueLabel = new Label();
        targetHeadingLabel = new Label();
        targetValueLabel = new Label();
        switchButton = new Button();
        statusLabel = new Label();
        SuspendLayout();

        titleLabel.AutoSize = true;
        titleLabel.Font = new Font("Segoe UI", 20F, FontStyle.Bold);
        titleLabel.Location = new Point(32, 28);
        titleLabel.Name = "titleLabel";
        titleLabel.Size = new Size(180, 37);
        titleLabel.Text = "CleanSwitch";

        currentHeadingLabel.AutoSize = true;
        currentHeadingLabel.Font = new Font("Segoe UI", 9F);
        currentHeadingLabel.ForeColor = Color.FromArgb(90, 90, 90);
        currentHeadingLabel.Location = new Point(36, 88);
        currentHeadingLabel.Name = "currentHeadingLabel";
        currentHeadingLabel.Text = "Current system:";

        currentValueLabel.AutoSize = false;
        currentValueLabel.AutoEllipsis = true;
        currentValueLabel.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        currentValueLabel.Location = new Point(36, 110);
        currentValueLabel.Name = "currentValueLabel";
        currentValueLabel.Size = new Size(348, 24);
        currentValueLabel.Text = "Detecting...";

        targetHeadingLabel.AutoSize = true;
        targetHeadingLabel.Font = new Font("Segoe UI", 9F);
        targetHeadingLabel.ForeColor = Color.FromArgb(90, 90, 90);
        targetHeadingLabel.Location = new Point(36, 154);
        targetHeadingLabel.Name = "targetHeadingLabel";
        targetHeadingLabel.Text = "Target:";

        targetValueLabel.AutoSize = false;
        targetValueLabel.AutoEllipsis = true;
        targetValueLabel.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        targetValueLabel.Location = new Point(36, 176);
        targetValueLabel.Name = "targetValueLabel";
        targetValueLabel.Size = new Size(348, 24);
        targetValueLabel.Text = "Detecting...";

        switchButton.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        switchButton.Location = new Point(36, 228);
        switchButton.Name = "switchButton";
        switchButton.Size = new Size(348, 44);
        switchButton.TabIndex = 0;
        switchButton.Text = "Switch boot";
        switchButton.UseVisualStyleBackColor = true;
        switchButton.Enabled = false;
        switchButton.Click += SwitchButton_Click;

        statusLabel.AutoSize = false;
        statusLabel.Font = new Font("Segoe UI", 9F);
        statusLabel.ForeColor = Color.FromArgb(70, 70, 70);
        statusLabel.Location = new Point(36, 284);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(348, 40);
        statusLabel.Text = string.Empty;

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(420, 340);
        Controls.Add(statusLabel);
        Controls.Add(switchButton);
        Controls.Add(targetValueLabel);
        Controls.Add(targetHeadingLabel);
        Controls.Add(currentValueLabel);
        Controls.Add(currentHeadingLabel);
        Controls.Add(titleLabel);
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "CleanSwitch";
        Load += MainForm_Load;
        ResumeLayout(false);
        PerformLayout();
    }
}
