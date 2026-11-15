param(
    [Parameter(Mandatory)]
    [ValidateSet('Clipboard', 'Settings', 'Quick')]
    [string]$Scenario,
    [Parameter(Mandatory)]
    [string]$AppDirectory
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This script modifies the clipboard and drives UI on a disposable GitHub-hosted Windows runner.'
}
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { throw 'Run in an STA.' }
if (Get-Process -Name Snipit,SharpSnip -ErrorAction SilentlyContinue) { throw 'Snipit or SharpSnip is already running.' }

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WindowsLabNative
{
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr previous, string className, string windowName);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@

$source = (Resolve-Path -LiteralPath $AppDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $source 'Snipit.exe') -PathType Leaf)) { throw 'Publish Snipit first.' }
$lab = Join-Path $env:RUNNER_TEMP "snipit-$Scenario-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$portable = New-Item -ItemType Directory -Path (Join-Path $lab 'app')
Copy-Item -Path (Join-Path $source '*') -Destination $portable.FullName -Recurse
$script:exe = Join-Path $portable.FullName 'Snipit.exe'
$data = New-Item -ItemType Directory -Path (Join-Path $portable.FullName 'data')
$script:profile = Join-Path $data.FullName 'settings.json'
$script:shots = New-Item -ItemType Directory -Path (Join-Path $lab 'captures')
$script:clipboardStaging = Join-Path $data.FullName 'clipboard'
$script:logPath = Join-Path $data.FullName 'logs'
$script:keyboardKey = 'HKEY_CURRENT_USER\Control Panel\Keyboard'
$script:keyboardValue = 'PrintScreenKeyForSnippingEnabled'
$script:automation = [System.Windows.Automation.AutomationElement]::RootElement

function Run-Snipit([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw "Unsafe lab argument: $argument" }
    }
    $info.Arguments = (($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $child = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (!$child.WaitForExit(30000)) {
            $child.Kill()
            throw "Owned Snipit process $($child.Id) timed out."
        }
        if ($child.ExitCode -ne 0) {
            throw "Owned Snipit process $($child.Id) exited $($child.ExitCode). $($stdout.Result) $($stderr.Result)"
        }
        if ($stdout.Result.Trim()) { Write-Host $stdout.Result.Trim() }
    } finally { $child.Dispose() }
}

function Get-JpegSize([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create($stream,
            [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        if ($decoder -isnot [Windows.Media.Imaging.JpegBitmapDecoder] -or $decoder.Frames.Count -ne 1) {
            throw "Expected a JPEG with one frame: $Path"
        }
        $frame = $decoder.Frames[0]
        if ($frame.PixelWidth -lt 1 -or $frame.PixelHeight -lt 1) { throw "JPEG has invalid dimensions: $Path" }
        "$($frame.PixelWidth)x$($frame.PixelHeight)"
    } finally { $stream.Dispose() }
}

function Assert-ClipboardImage([string]$ExpectedSize) {
    $bitmap = [Windows.Clipboard]::GetImage()
    if (!$bitmap -or "$($bitmap.PixelWidth)x$($bitmap.PixelHeight)" -ne $ExpectedSize) {
        throw "Clipboard did not expose a bitmap with dimensions $ExpectedSize."
    }
    $payload = [Windows.Clipboard]::GetDataObject().GetData('PNG', $false)
    if ($payload -is [byte[]]) { $payload = [IO.MemoryStream]::new($payload, $false) }
    if ($payload -isnot [IO.Stream]) { throw 'Clipboard did not expose PNG bytes.' }
    try {
        if ($payload.CanSeek) { $payload.Position = 0 }
        $decoder = [Windows.Media.Imaging.PngBitmapDecoder]::new($payload,
            [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $frame = $decoder.Frames[0]
        if ("$($frame.PixelWidth)x$($frame.PixelHeight)" -ne $ExpectedSize) {
            throw "Clipboard PNG did not match $ExpectedSize."
        }
    } finally { $payload.Dispose() }
}

function Find-ExplorerWindow($Shell, [string]$Path) {
    foreach ($candidate in $Shell.Windows()) {
        try {
            if ($candidate.Document.Folder.Self.Path -eq $Path -and
                [IO.Path]::GetFileName($candidate.FullName) -eq 'explorer.exe') { return $candidate }
        } catch { continue }
    }
}

function Wait-PastedJpeg([string]$Path, [string]$ExpectedHash, [string]$ExpectedSize, [int]$Attempts) {
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            try {
                $currentHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
                if ($currentHash -eq $ExpectedHash -and (Get-JpegSize $Path) -eq $ExpectedSize) { return $true }
            } catch [IO.IOException] {}
        }
        Start-Sleep -Milliseconds 200
    }
    $false
}

function Invoke-RealShellPaste([string]$Name, [string]$Leaf, [string]$ExpectedHash, [string]$ExpectedSize) {
    $shell = New-Object -ComObject Shell.Application
    $verbTarget = New-Item -ItemType Directory -Path (Join-Path $lab "$Name shell verb")
    $item = $shell.NameSpace($verbTarget.FullName).Self
    foreach ($verb in $item.Verbs()) {
        if (($verb.Name -replace '&','').Trim() -eq 'Paste') {
            $item.InvokeVerb($verb.Name)
            if (Wait-PastedJpeg (Join-Path $verbTarget.FullName $Leaf) $ExpectedHash $ExpectedSize 25) {
                if (@(Get-ChildItem -LiteralPath $verbTarget.FullName -Force -File).Count -ne 1) {
                    throw 'Shell Paste wrote an unexpected file set.'
                }
                Write-Host "Windows Shell FolderItem paste verified in $($verbTarget.FullName)."
                return
            }
            Write-Host 'The Shell FolderItem Paste verb did not produce a verified JPEG.'
            break
        }
    }

    $explorerTarget = New-Item -ItemType Directory -Path (Join-Path $lab "$Name Explorer")
    $shell.Open($explorerTarget.FullName)
    $view = $null
    try {
        for ($attempt = 0; $attempt -lt 60 -and !$view; $attempt++) {
            $view = Find-ExplorerWindow $shell $explorerTarget.FullName
            if (!$view) { Start-Sleep -Milliseconds 250 }
        }
        if (!$view) { throw "No Explorer window opened for $($explorerTarget.FullName)." }
        $window = [IntPtr][long]$view.HWND
        [void][WindowsLabNative]::SetForegroundWindow($window)
        if ([WindowsLabNative]::GetForegroundWindow() -ne $window) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($window)
            $root.SetFocus()
        }
        if ([WindowsLabNative]::GetForegroundWindow() -ne $window -or
            $view.Document.Folder.Self.Path -ne $explorerTarget.FullName) {
            throw 'Could not establish focus in the verified Explorer destination.'
        }
        [System.Windows.Forms.SendKeys]::SendWait('^v')
        if (!(Wait-PastedJpeg (Join-Path $explorerTarget.FullName $Leaf) $ExpectedHash $ExpectedSize 60)) {
            throw 'Explorer Ctrl+V did not produce the expected JPEG.'
        }
        if (@(Get-ChildItem -LiteralPath $explorerTarget.FullName -Force -File).Count -ne 1) {
            throw 'Explorer Ctrl+V wrote an unexpected file set.'
        }
        Write-Host "Windows Explorer Ctrl+V verified in $($explorerTarget.FullName)."
    } finally {
        if ($view -and $view.Document.Folder.Self.Path -eq $explorerTarget.FullName) { $view.Quit() }
    }
}

function Test-ClipboardCapture {
    $original = Join-Path $shots.FullName 'one-shot.jpg'
    Run-Snipit @('--fullscreen', '--monitor', '1', '--out', $original)
    $size = Get-JpegSize $original
    Assert-ClipboardImage $size
    $fileDrop = [Windows.Clipboard]::GetFileDropList()
    if ($fileDrop.Count -ne 1) { throw "Expected one clipboard file, found $($fileDrop.Count)." }
    $clipboardJpeg = $fileDrop[0]
    $leaf = [IO.Path]::GetFileName($clipboardJpeg)
    if ([IO.Path]::GetDirectoryName($clipboardJpeg) -ne $clipboardStaging -or
        [IO.Path]::GetExtension($leaf) -ne '.jpg' -or (Get-JpegSize $clipboardJpeg) -ne $size) {
        throw "Clipboard JPEG is outside the isolated profile or differs in format or dimensions: $clipboardJpeg"
    }
    $sha = (Get-FileHash -LiteralPath $clipboardJpeg -Algorithm SHA256).Hash
    Write-Host "Owned capture process exited. Clipboard exposes Bitmap, decoded PNG and JPEG file list at $size. JPEG SHA-256 $sha."

    Invoke-RealShellPaste 'first paste' $leaf $sha $size
    Invoke-RealShellPaste 'second paste' $leaf $sha $size
    $sequence = [WindowsLabNative]::GetClipboardSequenceNumber()
    if (!$sequence) { throw 'Windows did not report a clipboard sequence number.' }
    $uncopied = Join-Path $shots.FullName 'no-clipboard.jpg'
    Run-Snipit @('--fullscreen', '--monitor', '1', '--out', $uncopied, '--no-clipboard')
    if ((Get-JpegSize $uncopied) -ne $size -or [WindowsLabNative]::GetClipboardSequenceNumber() -ne $sequence) {
        throw '--no-clipboard changed the existing clipboard or failed to save the expected JPEG.'
    }
    Assert-ClipboardImage $size
    $fileDrop = [Windows.Clipboard]::GetFileDropList()
    if ($fileDrop.Count -ne 1 -or $fileDrop[0] -ne $clipboardJpeg -or
        (Get-FileHash -LiteralPath $fileDrop[0] -Algorithm SHA256).Hash -ne $sha) {
        throw '--no-clipboard did not preserve the existing file payload.'
    }
    Write-Host '--no-clipboard saved its own JPEG and preserved the observed clipboard sequence, JPEG, Bitmap and PNG.'
}

function Find-Settings([int]$OwnedPid) {
    $byName = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Snipit settings')
    $byPid = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $OwnedPid)
    $both = [System.Windows.Automation.AndCondition]::new($byName, $byPid)
    $automation.FindFirst([System.Windows.Automation.TreeScope]::Children, $both)
}

function Open-Settings([int]$OwnedPid) {
    Run-Snipit @()
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $window = Find-Settings $OwnedPid
        if ($window) { return $window }
        Start-Sleep -Milliseconds 250
    }
    throw "Settings did not open for owned Snipit process $OwnedPid."
}

function Find-Control($Window, [string]$Name, $Type, [switch]$Optional) {
    $byName = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $byType = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type)
    $both = [System.Windows.Automation.AndCondition]::new($byName, $byType)
    $control = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $both)
    if (!$control -and !$Optional) { throw "Missing Settings control named '$Name'." }
    $control
}

function Find-VerticalScroll($Window) {
    foreach ($item in $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
        $scroll = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and
            $scroll.Current.VerticallyScrollable) { return $scroll }
    }
    throw 'Compact Settings did not offer a vertically scrollable body.'
}

function Invoke-SettingsButton($Window, [int]$OwnedPid, [string]$Name) {
    $button = Find-Control $Window $Name ([System.Windows.Automation.ControlType]::Button)
    if ($button.Current.IsOffscreen -or !$button.Current.IsEnabled) { throw "$Name is unavailable." }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if (!(Find-Settings $OwnedPid)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Settings stayed open after $Name."
}

function Resize-Settings($Window) {
    $transform = $Window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    if (!$transform.Current.CanResize) { throw 'Settings cannot be resized through Windows UI Automation.' }
    $transform.Resize(640, 680)
    Start-Sleep -Milliseconds 500
    $bounds = $Window.Current.BoundingRectangle
    if ([Math]::Abs($bounds.Width - 640) -gt 20 -or [Math]::Abs($bounds.Height - 680) -gt 20) {
        throw "Settings did not resize to about 640x680 screen pixels: $bounds"
    }
    Write-Host "Settings Windows UI Automation bounds: $($bounds.Width)x$($bounds.Height)."
}

function Test-CompactSettings([int]$OwnedPid) {
    $window = Open-Settings $OwnedPid
    Resize-Settings $window
    $edit = [System.Windows.Automation.ControlType]::Edit
    $output = Find-Control $window 'Output folder' $edit
    $null = Find-Control $window 'Image format' ([System.Windows.Automation.ControlType]::ComboBox)
    $null = Find-Control $window 'Full-screen target' ([System.Windows.Automation.ControlType]::ComboBox)
    if ($output.Current.IsOffscreen -or !$output.Current.IsKeyboardFocusable) {
        throw 'The compact layout hid or disabled keyboard focus for Output folder.'
    }
    $cancelledFolder = Join-Path $lab 'cancelled edits'
    $output.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($cancelledFolder)
    $scroll = Find-VerticalScroll $window
    $scroll.SetScrollPercent(-1, 100)
    Start-Sleep -Milliseconds 300
    $lower = Find-Control $window 'FFmpeg override' $edit
    if ($lower.Current.IsOffscreen -or !$lower.Current.IsKeyboardFocusable) { throw 'Scrolling did not expose FFmpeg override.' }
    $lower.SetFocus()
    $lower.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('cancelled.exe')
    Invoke-SettingsButton $window $OwnedPid 'Cancel'
    $prefs = Get-Content -LiteralPath $profile -Raw | ConvertFrom-Json
    if ($prefs.OutputFolder -ne $shots.FullName -or $prefs.FfmpegPath -eq 'cancelled.exe') {
        throw 'Cancel wrote Settings changes.'
    }

    $window = Open-Settings $OwnedPid
    Resize-Settings $window
    $scroll = Find-VerticalScroll $window
    $scroll.SetScrollPercent(-1, 0)
    Start-Sleep -Milliseconds 300
    $savedFolder = Join-Path $lab 'saved edits'
    $output = Find-Control $window 'Output folder' $edit
    $output.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($savedFolder)
    Invoke-SettingsButton $window $OwnedPid 'Save'
    $prefs = Get-Content -LiteralPath $profile -Raw | ConvertFrom-Json
    if ($prefs.OutputFolder -ne $savedFolder) { throw 'Save did not persist the owned Output folder.' }
    Write-Host 'Compact Settings exposed named controls, scrolled to FFmpeg override, supported focus, and kept Cancel and Save actionable.'
}

function Test-QuickCapture([int]$OwnedPid) {
    $handle = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 50 -and $handle -eq [IntPtr]::Zero; $attempt++) {
        $candidate = [WindowsLabNative]::FindWindowEx([IntPtr](-3), [IntPtr]::Zero, $null, 'SnipitMessageWindow')
        $ownerPid = [uint32]0
        if ($candidate -ne [IntPtr]::Zero) {
            [void][WindowsLabNative]::GetWindowThreadProcessId($candidate, [ref]$ownerPid)
            if ($ownerPid -ne $OwnedPid) { throw 'The registered window does not belong to the owned Snipit process.' }
            $handle = $candidate
        } else { Start-Sleep -Milliseconds 200 }
    }
    if ($handle -eq [IntPtr]::Zero) { throw 'Quick Mode message-only window was unavailable.' }
    if (![WindowsLabNative]::PostMessage($handle, 0x312, [IntPtr]1, [IntPtr]0)) {
        throw 'Posting WM_HOTKEY to the owned Snipit process failed.'
    }
    $capture = $null
    for ($attempt = 0; $attempt -lt 75 -and !$capture; $attempt++) {
        $capture = Get-ChildItem -LiteralPath $shots.FullName -Filter '*.jpg' -File | Select-Object -First 1
        if (!$capture) { Start-Sleep -Milliseconds 200 }
    }
    if (!$capture) { throw 'The Quick Mode handler did not write a JPEG.' }
    Start-Sleep -Milliseconds 800
    $size = Get-JpegSize $capture.FullName
    Assert-ClipboardImage $size
    $messages = @(Get-ChildItem -LiteralPath $logPath -File | ForEach-Object { Get-Content -LiteralPath $_.FullName })
    if (!($messages -match 'Snipit started') -or !($messages -match 'capture backend:')) {
        throw 'The app log did not record the owned Quick Mode startup and capture.'
    }
    if ($messages -match 'Copied to clipboard:|Clipboard unavailable:|JPEG file paste unavailable:|Capture failed:') {
        throw "Quick Mode logged a capture-result notification: $($messages -match 'Copied to clipboard:|Clipboard unavailable:|JPEG file paste unavailable:|Capture failed:')"
    }
    Write-Host "Owned WM_HOTKEY Quick Mode handler saved and copied a $size JPEG. AppHost notification log has no capture result message."
    Write-Host 'This checks Quick Mode dispatch and the notification log. Physical Print Screen and normal picker input were outside this hosted check.'
}

@{
    OutputFolder = $shots.FullName
    ImageFormat = 'jpg'
    ShowToasts = $true
    ShowTrayIcon = $false
    FixedWidth = 735
    QuickMode = $Scenario -eq 'Quick'
} | ConvertTo-Json | Set-Content -LiteralPath $profile -Encoding UTF8

if ($Scenario -eq 'Clipboard') {
    Test-ClipboardCapture
} else {
    $previousPrintScreen = [Microsoft.Win32.Registry]::GetValue($keyboardKey, $keyboardValue, $null)
    $process = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 60 -and !$ready; $attempt++) {
            if ($process.HasExited) { throw "Owned Snipit process exited $($process.ExitCode) before startup." }
            Start-Sleep -Milliseconds 200
            try { $prefs = Get-Content -LiteralPath $profile -Raw | ConvertFrom-Json }
            catch [IO.IOException] { continue }
            $ready = $prefs.PrintScreenPriorRecorded -eq $true -and
                [Microsoft.Win32.Registry]::GetValue($keyboardKey, $keyboardValue, $null) -eq 0
        }
        if (!$ready -or $prefs.FixedWidth -ne 735) { throw 'Snipit did not load its isolated portable profile.' }
        if ($Scenario -eq 'Settings') { Test-CompactSettings $process.Id }
        if ($Scenario -eq 'Quick') { Test-QuickCapture $process.Id }
    } finally {
        if (!$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
        $process.Dispose()
        Run-Snipit @('--restore-printscreen')
        if ([Microsoft.Win32.Registry]::GetValue($keyboardKey, $keyboardValue, $null) -ne $previousPrintScreen) {
            throw 'Snipit did not restore the runner Print Screen setting.'
        }
    }
}
