using System.Security.AccessControl;
using System.Security.Principal;

namespace CleanSwitch.Recovery;

internal sealed record WinReWorkspaceValidationFacts(
    bool IsLocalAbsolutePath,
    bool IsFixedDrive,
    string FileSystem,
    bool HasReparsePoint,
    bool IsCompressed,
    bool IsEncrypted,
    bool MountDirectoryEmpty,
    long AvailableFreeBytes,
    bool SystemHasFullControl,
    bool AdministratorsHaveFullControl);

internal static class WinReWorkspaceValidator
{
    internal const long AbsoluteMinimumFreeBytes = 8L * 1024 * 1024 * 1024;

    public static long RequiredFreeBytes(long sourceWimBytes)
    {
        if (sourceWimBytes <= 0)
        {
            throw new InvalidOperationException("The source WinRE WIM size must be positive.");
        }

        return Math.Max(AbsoluteMinimumFreeBytes, checked(sourceWimBytes * 6));
    }

    public static void Validate(WinReWorkspaceValidationFacts facts, long requiredFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var failures = new List<string>();
        if (!facts.IsLocalAbsolutePath) failures.Add("workspace path is not a local absolute path");
        if (!facts.IsFixedDrive) failures.Add("workspace is not on a local fixed drive");
        if (!string.Equals(facts.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)) failures.Add("workspace is not NTFS");
        if (facts.HasReparsePoint) failures.Add("workspace contains a reparse point");
        if (facts.IsCompressed) failures.Add("workspace is compressed");
        if (facts.IsEncrypted) failures.Add("workspace is encrypted");
        if (!facts.MountDirectoryEmpty) failures.Add("mount directory is not empty");
        if (facts.AvailableFreeBytes < requiredFreeBytes)
            failures.Add($"workspace has {facts.AvailableFreeBytes} free bytes but requires at least {requiredFreeBytes}");
        if (!facts.SystemHasFullControl) failures.Add("SYSTEM does not have FullControl");
        if (!facts.AdministratorsHaveFullControl) failures.Add("Administrators do not have FullControl");

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "WinRE servicing workspace validation failed closed: " + string.Join("; ", failures) + ".");
        }
    }
}

internal interface IWinReWorkspaceFactory
{
    WinReServicingWorkspace Create(long sourceWimBytes);
}

internal sealed class WindowsWinReWorkspaceFactory : IWinReWorkspaceFactory
{
    internal static string ApplicationRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CleanSwitch");

    internal static string MachineRoot => Path.Combine(
        ApplicationRoot,
        "WinRE");

    public WinReServicingWorkspace Create(long sourceWimBytes)
    {
        var machineRoot = Path.GetFullPath(MachineRoot);
        var operationRoot = Path.Combine(machineRoot, "operation-" + Guid.NewGuid().ToString("N"));
        var workspace = new WinReServicingWorkspace(machineRoot, operationRoot);

        try
        {
            ValidateLocalFixedNtfsRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            CreateSecuredDirectory(ApplicationRoot);
            CreateSecuredDirectory(machineRoot);
            CreateSecuredDirectory(operationRoot);
            CreateSecuredDirectory(workspace.SourceCopyDirectory);
            CreateSecuredDirectory(workspace.MountDirectory);
            CreateSecuredDirectory(workspace.ScratchDirectory);
            CreateSecuredDirectory(workspace.LogDirectory);

            var facts = Inspect(workspace);
            WinReWorkspaceValidator.Validate(facts, WinReWorkspaceValidator.RequiredFreeBytes(sourceWimBytes));
            return workspace;
        }
        catch
        {
            workspace.TryCleanupOwned(out _);
            throw;
        }
    }

    private static WinReWorkspaceValidationFacts Inspect(WinReServicingWorkspace workspace)
    {
        var full = Path.GetFullPath(workspace.OperationRoot);
        var root = Path.GetPathRoot(full);
        var isLocal = Path.IsPathFullyQualified(full) &&
                      !full.StartsWith(@"\\", StringComparison.Ordinal) &&
                      !string.IsNullOrWhiteSpace(root);
        var drive = isLocal ? new DriveInfo(root!) : null;
        var attributes = new[]
        {
            workspace.MachineRoot,
            workspace.OperationRoot,
            workspace.SourceCopyDirectory,
            workspace.MountDirectory,
            workspace.ScratchDirectory,
            workspace.LogDirectory
        }.Select(File.GetAttributes).ToArray();

        var securedPaths = new[]
        {
            workspace.OperationRoot,
            workspace.SourceCopyDirectory,
            workspace.MountDirectory,
            workspace.ScratchDirectory,
            workspace.LogDirectory
        };
        var access = securedPaths.Select(ReadRequiredAccess).ToArray();
        return new WinReWorkspaceValidationFacts(
            isLocal,
            drive?.DriveType == DriveType.Fixed,
            drive?.DriveFormat ?? string.Empty,
            attributes.Any(value => value.HasFlag(FileAttributes.ReparsePoint)),
            attributes.Any(value => value.HasFlag(FileAttributes.Compressed)),
            attributes.Any(value => value.HasFlag(FileAttributes.Encrypted)),
            !Directory.EnumerateFileSystemEntries(workspace.MountDirectory).Any(),
            drive?.AvailableFreeSpace ?? 0,
            access.All(value => value.System),
            access.All(value => value.Administrators));
    }

    private static void CreateSecuredDirectory(string path)
    {
        if (File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException($"WinRE workspace path is occupied by a file: '{path}'.");
        }

        if (Directory.Exists(path))
        {
            RejectUnsafeAttributes(path);
        }

        Directory.CreateDirectory(path);
        RejectUnsafeAttributes(path);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
        RejectUnsafeAttributes(path);
    }

    private static void ValidateLocalFixedNtfsRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The machine-level WinRE workspace root is not a local absolute path.");
        }

        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Fixed ||
            !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The machine-level WinRE workspace root must be on a local fixed NTFS drive.");
        }
    }

    private static void RejectUnsafeAttributes(string path)
    {
        var attributes = File.GetAttributes(path);
        const FileAttributes rejected =
            FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Encrypted;
        if ((attributes & rejected) != 0)
        {
            throw new InvalidOperationException(
                $"WinRE workspace path '{path}' has rejected attributes '{attributes & rejected}'.");
        }
    }

    private static (bool System, bool Administrators) ReadRequiredAccess(string path)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rules = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();

        bool HasFullControl(SecurityIdentifier sid) => rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            sid.Equals(rule.IdentityReference) &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

        return (HasFullControl(system), HasFullControl(administrators));
    }
}

internal sealed class WinReServicingWorkspace
{
    public WinReServicingWorkspace(string machineRoot, string operationRoot)
    {
        MachineRoot = Path.GetFullPath(machineRoot);
        OperationRoot = Path.GetFullPath(operationRoot);
        SourceCopyDirectory = Path.Combine(OperationRoot, "source-copy");
        MountDirectory = Path.Combine(OperationRoot, "mount");
        ScratchDirectory = Path.Combine(OperationRoot, "scratch");
        LogDirectory = Path.Combine(OperationRoot, "logs");
        PreparedImagePath = Path.Combine(SourceCopyDirectory, "Winre.wim");
    }

    public string MachineRoot { get; }
    public string OperationRoot { get; }
    public string SourceCopyDirectory { get; }
    public string MountDirectory { get; }
    public string ScratchDirectory { get; }
    public string LogDirectory { get; }
    public string PreparedImagePath { get; }

    public string CreateDismLogPath(string operation) =>
        Path.Combine(LogDirectory, $"dism-{operation}-{Guid.NewGuid():N}.log");

    public void RequireEmptyMountDirectory()
    {
        if (!Directory.Exists(MountDirectory) || Directory.EnumerateFileSystemEntries(MountDirectory).Any())
        {
            throw new InvalidOperationException(
                $"WinRE mount directory must exist and be empty: '{MountDirectory}'.");
        }
    }

    public bool TryCleanupOwned(out string diagnostic)
    {
        var root = MachineRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(OperationRoot);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("operation-", StringComparison.Ordinal))
        {
            diagnostic = $"Refused cleanup of unowned WinRE workspace '{full}'.";
            return false;
        }

        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }

            diagnostic = $"Removed owned WinRE workspace '{full}'.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostic =
                $"Owned WinRE workspace residue remains at '{full}': {exception.Message} " +
                "No global DISM cleanup was attempted.";
            return false;
        }
    }
}
