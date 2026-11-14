using System.Security;
using Microsoft.Win32;

namespace Snipit;

/// <summary>Manages the per-user startup entry shared with the installer.</summary>
internal static class StartAtLogin
{
    private const string InstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SeanPak-Snipit";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Snipit";

    public static (bool? Enabled, string? Warning) Read()
    {
        try
        {
            if (InstalledCommand() is not { } command) return (null, null);
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            object? current = key?.GetValue(RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return (Equals(current, command), null);
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
            if (InstalledCommand() is not { } command)
                return "Start at login requires an installed copy. Reopen Settings from the installed Snipit.";
            if (enabled && command.Length > 260)
                return "The Snipit install path is too long for Windows startup. Install Snipit at a shorter path.";
            using var key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey)
                : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return null;
            object? current = key.GetValue(RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (enabled)
            {
                if (Equals(current, command)) return null;
                if (current is not null)
                    return "A different Windows startup entry uses the name Snipit. Remove that entry before enabling Start at login.";
                key.SetValue(RunName, command, RegistryValueKind.String);
            }
            else if (Equals(current, command))
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

    private static string? InstalledCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallKey);
        if (key?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            is not string directory || !Path.IsPathFullyQualified(directory))
            return null;
        string exe = Path.Combine(directory, "Snipit.exe");
        return string.Equals(Path.GetFullPath(exe), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            ? $"\"{exe}\"" : null;
    }
}
