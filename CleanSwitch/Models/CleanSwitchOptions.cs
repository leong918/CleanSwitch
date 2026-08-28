namespace CleanSwitch.Models;

public sealed class CleanSwitchOptions
{
    public string Boot2Guid { get; set; } = string.Empty;

    public int RestartDelaySeconds { get; set; } = 5;
}
