param([Parameter(Mandatory)][int]$OwnedPid)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SnipitTrayWindow {
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    public static int RequestTrayContextMenu(int ownedPid) {
        const uint notifyIconCallback = 0x800;
        const int rightButtonUp = 0x205;
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr window, IntPtr parameter) {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid == ownedPid) {
                var name = new StringBuilder(256);
                GetClassName(window, name, name.Capacity);
                if (name.ToString().StartsWith("WindowsForms10.Window.0.", StringComparison.Ordinal))
                    windows.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        int posted = 0;
        foreach (IntPtr window in windows) {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid == ownedPid && PostMessage(window, notifyIconCallback, new UIntPtr(1u), new IntPtr(rightButtonUp)))
                ++posted;
        }
        return posted;
    }
}
'@

$process = [Diagnostics.Process]::GetProcessById($OwnedPid)
try {
    if ($process.ProcessName -cne 'Snipit' -or $process.SessionId -ne (Get-Process -Id $PID).SessionId) {
        throw 'The requested process is not Snipit in the current Windows session'
    }
    if ([SnipitTrayWindow]::RequestTrayContextMenu($OwnedPid) -eq 0) {
        throw 'No owned WinForms tray callback window was found'
    }

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pidCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $OwnedPid)
    $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Exit')
    $typeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $condition = [System.Windows.Automation.AndCondition]::new($pidCondition, $nameCondition, $typeCondition)
    $search = [Diagnostics.Stopwatch]::StartNew()
    do {
        $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($item) { break }
        Start-Sleep -Milliseconds 50
    } while ($search.ElapsedMilliseconds -lt 4000 -and !$process.HasExited)
    if (!$item) { throw 'The owned tray menu did not expose its Exit command' }

    $action = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$action)) {
        $method = 'InvokePattern'
    } elseif ($item.TryGetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern, [ref]$action)) {
        $method = 'LegacyIAccessiblePattern'
    } else {
        throw 'The owned tray Exit item has no UI Automation action'
    }

    # Retain a process handle before invoking the command. Windows PowerShell's
    # .NET Framework can lose exit details if it first opens the PID afterward.
    $null = $process.Handle
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($method -eq 'InvokePattern') { $action.Invoke() } else { $action.DoDefaultAction() }
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        if (!$process.WaitForExit(500)) { throw }
    }
    if (!$process.WaitForExit(10000)) { throw 'Snipit did not exit after invoking its tray command' }
    [pscustomobject]@{
        method = "Actual tray Exit via $method"
        exitMs = [math]::Round($clock.Elapsed.TotalMilliseconds, 3)
        exitCode = $process.ExitCode
    } | ConvertTo-Json -Compress
} finally {
    $process.Dispose()
}
