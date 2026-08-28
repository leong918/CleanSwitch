using CleanSwitch.Models;

namespace CleanSwitch.Services;

public interface IBootManager
{
    Task<BootLayout> DetectAsync(string? preferredOtherGuid);

    Task<bool> SetNextBootAsync(string bootGuid);

    Task RestartAsync(int delaySeconds);
}
