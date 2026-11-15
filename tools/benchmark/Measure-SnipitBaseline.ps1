param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$PortableZipPath,
    [Parameter(Mandatory)][string]$ChecksumManifestPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag = 'v0.28.1',
    [ValidateRange(5, 20)][int]$Trials = 5,
    [ValidateRange(20, 120)][int]$IdleSeconds = 20
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This harness is restricted to a disposable GitHub-hosted Windows runner'
}
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Evidence directory must be new' }
$evidence = (New-Item -ItemType Directory -Path $EvidenceDirectory).FullName
$lab = (New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP "snipit-baseline-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT")).FullName
$installedRoot = Join-Path $lab 'installed'
$portableRoot = Join-Path $lab 'portable'
$arpKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SeanPak-Snipit'
$runKey = 'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run'
$keyboardKey = 'HKEY_CURRENT_USER\Control Panel\Keyboard'
$printScreenName = 'PrintScreenKeyForSnippingEnabled'
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Invoke-OwnedProcess([string]$Program, [string[]]$Arguments, [int]$TimeoutMs = 120000) {
    $info = [Diagnostics.ProcessStartInfo]::new($Program)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (!$child.WaitForExit($TimeoutMs)) {
            $child.Kill($true)
            throw "Timed out: $Program"
        }
        if ($child.ExitCode -ne 0) {
            throw "$Program exited $($child.ExitCode): $($stderr.Result) $($stdout.Result)"
        }
        $stdout.Result
    } finally {
        $child.Dispose()
    }
}

function Read-PrintScreen {
    [Microsoft.Win32.Registry]::GetValue($keyboardKey, $printScreenName, $null)
}

function Get-Footprint([string]$Path) {
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force)
    [ordered]@{
        files = $files.Count
        logicalBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
    }
}

function Get-Distribution([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    $half = [math]::Floor($ordered.Count / 2)
    $median = if ($ordered.Count % 2) { $ordered[$half] } else { ($ordered[$half - 1] + $ordered[$half]) / 2 }
    [ordered]@{ count = $ordered.Count; median = $median; min = $ordered[0]; max = $ordered[-1] }
}

function Read-Log([string]$ProfilePath) {
    $logs = Join-Path $ProfilePath 'logs'
    if (Test-Path -LiteralPath $logs) {
        $files = [IO.Directory]::GetFiles($logs, '*.log')
        foreach ($file in $files) {
            try { [IO.File]::ReadAllText($file) } catch [IO.IOException] {}
        }
    }
}

function Wait-ForStartup([Diagnostics.Process]$Process, [Diagnostics.Stopwatch]$Clock, [string]$ProfilePath) {
    while ($Clock.ElapsedMilliseconds -lt 12000) {
        if ($Process.HasExited) { throw "Snipit exited before its startup marker (exit $($Process.ExitCode))" }
        if ((Read-Log $ProfilePath) -match '(?m)\[INFO\] Snipit started\s*$') {
            return [math]::Round($Clock.Elapsed.TotalMilliseconds, 3)
        }
        [Threading.Thread]::Sleep(8)
    }
    throw 'No Snipit started log marker appeared in 12 seconds'
}

function Measure-Idle([Diagnostics.Process]$Process, [int]$Seconds, [int]$LogicalCpuCount) {
    Start-Sleep -Seconds 3
    $Process.Refresh()
    $cpuBefore = $Process.TotalProcessorTime.TotalMilliseconds
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $samples = @(for ($sample = 0; $sample -lt $Seconds; $sample += 2) {
        Start-Sleep -Seconds 2
        $Process.Refresh()
        [ordered]@{
            atSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 3)
            workingSetBytes = $Process.WorkingSet64
            privateBytes = $Process.PrivateMemorySize64
            threads = $Process.Threads.Count
            handles = $Process.HandleCount
        }
    })
    $cpuMs = $Process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
    $elapsed = $clock.Elapsed.TotalSeconds
    [ordered]@{
        seconds = [math]::Round($elapsed, 3)
        cpuMs = [math]::Round($cpuMs, 3)
        cpuMsPerSecond = [math]::Round($cpuMs / $elapsed, 6)
        cpuPercentWholeMachine = [math]::Round($cpuMs / ($elapsed * 10 * $LogicalCpuCount), 6)
        workingSetBytes = Get-Distribution @($samples.workingSetBytes)
        privateBytes = Get-Distribution @($samples.privateBytes)
        handles = Get-Distribution @($samples.handles)
        threads = Get-Distribution @($samples.threads)
        samples = $samples
    }
}

if ((Test-Path -LiteralPath $arpKey) -or
    $null -ne [Microsoft.Win32.Registry]::GetValue($runKey, 'Snipit', $null) -or
    (Get-Process -Name Snipit,SharpSnip -ErrorAction SilentlyContinue)) {
    throw 'Runner already has Snipit registration or a running Snipit or SharpSnip process'
}
$setup = Get-Item -LiteralPath $InstallerPath
$zip = Get-Item -LiteralPath $PortableZipPath
$checksumText = Get-Content -LiteralPath $ChecksumManifestPath
$artifactSizes = foreach ($file in @($setup, $zip)) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ($checksumText -notcontains "$hash  $($file.Name)") {
        throw "Published manifest SHA-256 mismatch: $($file.Name)"
    }
    [ordered]@{ name = $file.Name; bytes = $file.Length; sha256 = $hash }
}
$computer = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$cpu = @(Get-CimInstance Win32_Processor)
$hostInfo = [ordered]@{
    osName = $os.Caption
    osVersion = $os.Version
    osBuild = $os.BuildNumber
    runnerImage = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
    architecture = $env:PROCESSOR_ARCHITECTURE
    model = $computer.Model
    cpuNames = @($cpu.Name)
    logicalProcessors = $computer.NumberOfLogicalProcessors
    machineRamBytes = $computer.TotalPhysicalMemory
    powerShell = $PSVersionTable.PSVersion.ToString()
}

$size = [ordered]@{ packages = $artifactSizes }
try {
    $installerArgs = @('--install', '--to', $installedRoot, '--json', '--silent')
    Invoke-OwnedProcess $setup.FullName $installerArgs | Out-Host
    $installedExe = Join-Path $installedRoot 'Snipit.exe'
    if (!(Test-Path -LiteralPath $installedExe) -or !(Test-Path -LiteralPath $arpKey)) {
        throw 'Silent install did not produce its executable and per-user registration'
    }
    $size.installed = Get-Footprint $installedRoot
    $size.installed.uninstallerBytes = (Get-Item -LiteralPath (Join-Path $installedRoot 'Uninstall.exe')).Length
} finally {
    if ((Test-Path -LiteralPath $installedRoot) -and (Test-Path -LiteralPath $arpKey)) {
        Invoke-OwnedProcess $setup.FullName @('--uninstall', '--to', $installedRoot, '--json', '--silent') | Out-Host
    }
}
if ((Test-Path -LiteralPath $arpKey) -or
    $null -ne [Microsoft.Win32.Registry]::GetValue($runKey, 'Snipit', $null) -or
    (Test-Path -LiteralPath (Join-Path $installedRoot 'Snipit.exe'))) {
    throw 'The installed fixture was not removed'
}

$unpackClock = [Diagnostics.Stopwatch]::StartNew()
[IO.Compression.ZipFile]::ExtractToDirectory($zip.FullName, $portableRoot)
$size.extractionMs = [math]::Round($unpackClock.Elapsed.TotalMilliseconds, 3)
$size.extractedPortable = Get-Footprint $portableRoot
$size | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'size.json')
$size | ConvertTo-Json -Depth 6 | Write-Host
$hostInfo | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'host.json')

$exe = Join-Path $portableRoot 'Snipit.exe'
$profilePath = Join-Path $portableRoot 'data'
$prior = Read-PrintScreen
$trialsRaw = for ($index = 1; $index -le $Trials; $index++) {
    New-Item -ItemType Directory -Path $profilePath | Out-Null
    $launch = [Diagnostics.ProcessStartInfo]::new($exe)
    $launch.UseShellExecute = $false
    $launch.WorkingDirectory = $portableRoot
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($launch)
    try {
        $readyMs = Wait-ForStartup $process $elapsed $profilePath
        if ((Read-PrintScreen) -ne 0) { throw 'Startup did not apply the Print Screen override' }
        $idle = Measure-Idle $process $IdleSeconds $hostInfo.logicalProcessors
        [ordered]@{trial = $index; readyMs = $readyMs; idle = $idle} |
            ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $evidence "trial-$index.json")
        $driver = Join-Path $PSScriptRoot 'Invoke-SnipitTrayExit.ps1'
        $driverOutput = Invoke-OwnedProcess $windowsPowerShell @('-NoProfile','-NonInteractive','-STA','-ExecutionPolicy','Bypass',
            '-File', $driver, '-OwnedPid', $process.Id.ToString()) 25000
        if (!$driverOutput) { throw 'Tray Exit driver emitted no timing evidence' }
        $exit = $driverOutput | ConvertFrom-Json
        Write-Host "Tray Exit driver: $driverOutput"
        if (!$process.WaitForExit(1000)) { throw 'Tray Exit did not end the launched process' }
        if ($process.ExitCode -ne 0) {
            Read-Log $profilePath | Write-Host
            throw "Tray Exit ended the launched process with exit code $($process.ExitCode)"
        }
        if ((Read-Log $profilePath) -notmatch '(?m)\[INFO\] Snipit stopped\s*$') {
            throw 'Graceful exit did not produce its stop marker'
        }
        $prefs = Get-Content -LiteralPath (Join-Path $profilePath 'settings.json') -Raw | ConvertFrom-Json
        if ($prefs.PrintScreenPriorRecorded -ne $false -or (Read-PrintScreen) -ne $prior) {
            throw 'Graceful exit did not restore the original Print Screen state'
        }
        $trial = [ordered]@{
            trial = $index
            launch = if ($index -eq 1) { 'First launch after extraction, OS cache uncontrolled' }
                else { 'Fresh process, subsequent launch with uncontrolled OS cache' }
            readyMs = $readyMs
            idle = $idle
            exitMs = $exit.exitMs
            exitMethod = $exit.method
            exitCode = $exit.exitCode
            printScreenRestored = $true
            stateBytesAfterExit = (Get-Footprint $profilePath).logicalBytes
        }
        $trial | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $evidence "trial-$index.json")
        Write-Host "Trial ${index}: ready $readyMs ms, exit $($exit.exitMs) ms, idle CPU $($idle.cpuMsPerSecond) ms/s"
        $trial
    } finally {
        if (!$process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
            Invoke-OwnedProcess $exe @('--restore-printscreen') | Out-Host
        }
        $process.Dispose()
        Move-Item -LiteralPath $profilePath -Destination (Join-Path $lab "profile-$index")
    }
}
$repeated = @($trialsRaw | Select-Object -Skip 1)
$summary = [ordered]@{
    schema = 1
    tag = $Tag
    host = $hostInfo
    size = $size
    startupFirstMs = $trialsRaw[0].readyMs
    startupSubsequentMs = Get-Distribution @($repeated.readyMs)
    gracefulExitMs = Get-Distribution @($trialsRaw.exitMs)
    idleWorkingSetBytes = Get-Distribution @($trialsRaw | ForEach-Object { $_.idle.workingSetBytes.median })
    idlePrivateBytes = Get-Distribution @($trialsRaw | ForEach-Object { $_.idle.privateBytes.median })
    idleCpuMsPerSecond = Get-Distribution @($trialsRaw | ForEach-Object { $_.idle.cpuMsPerSecond })
    idleCpuPercentWholeMachine = Get-Distribution @($trialsRaw | ForEach-Object { $_.idle.cpuPercentWholeMachine })
    idleHandles = Get-Distribution @($trialsRaw | ForEach-Object { $_.idle.handles.median })
    idleThreads = Get-Distribution @($trialsRaw | ForEach-Object { $_.idle.threads.median })
    startupEndpoint = 'Process.Start to observation of Snipit started log, upper-bound sampling'
    idleSettleSeconds = 3
    idleSampleIntervalSeconds = 2
    exitEndpoint = 'UI Automation invocation of actual tray Exit item to process exit'
    cachePolicy = 'Uncontrolled OS cache. Fresh Snipit process and adjacent data per trial'
}
$summary | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json')
$summary | ConvertTo-Json -Depth 9 | Write-Host
