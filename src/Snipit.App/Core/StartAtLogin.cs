using System.Security;
using Microsoft.Win32;

namespace Snipit;

/// <summary>Manages the per-user startup entry shared with the installer.</summary>
internal static class StartAtLogin
{
    private const string InstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Snipit";
    // Deprecated: the key SeanPak's uninstaller wrote, still honoured for installs it made.
    private const string LegacyInstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SeanPak-Snipit";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Snipit";

    public static (bool? Enabled, string? Warning) Read()
    {
        try
        {
            if (InstalledExecutable() is not { } exe) return (null, null);
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            object? current = key?.GetValue(RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return (OwnsCommand(current, exe), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            Log.Error("Could not read Windows startup settings", ex);
            return (null, "Could not read Start at login. Check whether Windows allows access to startup settings, then reopen this window.");
        }
    }

    public static string? SetEnabled(bool enabled)
    {
        try
        {
            if (InstalledExecutable() is not { } exe)
                return "Start at login requires an installed copy. Reopen Settings from the installed Snipit.";
            string command = $"\"{exe}\"";
            if (enabled && command.Length > 260)
                return "The Snipit install path is too long for Windows startup. Install Snipit at a shorter path.";
            using var key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey)
                : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return null;
            object? current = key.GetValue(RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            bool owned = OwnsCommand(current, exe);
            if (enabled)
            {
                if (owned) return null;
                if (current is not null)
                    return "A different Windows startup entry uses the name Snipit. Remove that entry before enabling Start at login.";
                key.SetValue(RunName, command, RegistryValueKind.String);
            }
            else if (owned)
            {
                key.DeleteValue(RunName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            Log.Error("Could not change Windows startup settings", ex);
            return "Could not change Start at login. Check whether Windows allows changes to startup settings and try again.";
        }
    }

    private static bool OwnsCommand(object? value, string exe)
    {
        if (value is not string { Length: > 2 } command || command[0] != '"' || command[^1] != '"')
            return false;
        string path = command[1..^1];
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || path.Contains('*') || path.Contains('?'))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? InstalledExecutable()
    {
        foreach (var keyPath in new[] { InstallKey, LegacyInstallKey })
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                is not string directory || !Path.IsPathFullyQualified(directory))
                continue;
            string exe = Path.Combine(directory, "Snipit.exe");
            if (string.Equals(Path.GetFullPath(exe), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return exe.Replace('/', '\\');
        }
        return null;
    }
}
