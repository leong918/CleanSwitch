namespace CleanSwitch.Models;

public sealed record BootEntry(string Identifier, string Description);

public sealed record BootLayout(BootEntry Current, BootEntry Target);
