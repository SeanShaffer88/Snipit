param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$PortableZipPath,
    [Parameter(Mandatory)][string]$ChecksumManifestPath,
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateSet('Fastest', 'Optimal')][string]$RuntimeCandidate = 'Optimal'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

function Get-EntryHash([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}

function Copy-ZipWithCompression(
    [IO.Compression.ZipArchive]$Source,
    [string]$Destination,
    [IO.Compression.CompressionLevel]$Level
) {
    $output = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $Source.Entries) {
            $entry = $output.CreateEntry($file.FullName, $Level)
            $entry.LastWriteTime = $file.LastWriteTime
            $entry.ExternalAttributes = $file.ExternalAttributes
            $inputStream = $file.Open()
            try {
                $outputStream = $entry.Open()
                try { $inputStream.CopyTo($outputStream) }
                finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
        }
    } finally { $output.Dispose() }
}

function Measure-Extraction([string]$Archive, [string]$Root, [Diagnostics.Process]$Process) {
    $Process.Refresh()
    $cpuBefore = $Process.TotalProcessorTime.TotalMilliseconds
    $clock = [Diagnostics.Stopwatch]::StartNew()
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Root)
    $clock.Stop()
    $Process.Refresh()
    [ordered]@{
        elapsedMs = [math]::Round($clock.Elapsed.TotalMilliseconds, 3)
        cpuMs = [math]::Round($Process.TotalProcessorTime.TotalMilliseconds - $cpuBefore, 3)
    }
}

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $sorted[[int][math]::Floor($sorted.Count / 2)]
}

if ((Test-Path -LiteralPath $CandidateDirectory) -or (Test-Path -LiteralPath $EvidenceDirectory)) {
    throw 'Candidate and evidence directories must both be new'
}
$evidence = (New-Item -ItemType Directory -Path $EvidenceDirectory).FullName
$candidates = (New-Item -ItemType Directory -Path $CandidateDirectory).FullName
$installer = Get-Item -LiteralPath $InstallerPath
$portable = Get-Item -LiteralPath $PortableZipPath
$manifest = @(Get-Content -LiteralPath $ChecksumManifestPath)
if ($manifest.Count -ne 2) { throw 'Unexpected published checksum manifest count' }
$installerLine = "$( (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash )  $($installer.Name)"
$portableLine = "$( (Get-FileHash -LiteralPath $portable.FullName -Algorithm SHA256).Hash )  $($portable.Name)"
if ($manifest -notcontains $installerLine -or $manifest -notcontains $portableLine) {
    throw 'Published package SHA-256 did not match the input manifest'
}

$archives = [ordered]@{ Stored = $portable.FullName }
$entryProof = [Collections.Generic.List[object]]::new()
$logicalBytes = 0L
$source = [IO.Compression.ZipFile]::OpenRead($portable.FullName)
try {
    if ($source.Entries.Count -ne 487) { throw 'Expected exactly 487 published ZIP entries' }
    foreach ($entry in $source.Entries) {
        $entryProof.Add([ordered]@{ path = $entry.FullName; sha256 = (Get-EntryHash $entry); bytes = $entry.Length })
        $logicalBytes += $entry.Length
    }
    foreach ($mode in @('Fastest', 'Optimal')) {
        $candidate = (New-Item -ItemType Directory -Path (Join-Path $candidates $mode)).FullName
        $path = Join-Path $candidate $portable.Name
        Copy-ZipWithCompression $source $path ([IO.Compression.CompressionLevel]$mode)
        $check = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            if ($check.Entries.Count -ne $source.Entries.Count) { throw "Entry count changed in $mode" }
            for ($index = 0; $index -lt $source.Entries.Count; $index++) {
                $before = $source.Entries[$index]
                $after = $check.Entries[$index]
                if ($after.FullName -cne $before.FullName -or $after.Length -ne $before.Length -or
                    $after.LastWriteTime -ne $before.LastWriteTime -or $after.ExternalAttributes -ne $before.ExternalAttributes -or
                    (Get-EntryHash $after) -cne $entryProof[$index].sha256) {
                    throw "Entry parity failed at index $index in $mode"
                }
            }
        } finally { $check.Dispose() }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $updatedManifest = foreach ($line in $manifest) {
            if ($line -ieq $portableLine) { "$hash  $($portable.Name)" } else { $line }
        }
        $updatedManifest | Set-Content -LiteralPath (Join-Path $candidate 'SHA256SUMS.txt') -Encoding ascii
        $archives[$mode] = $path
    }
} finally { $source.Dispose() }

$process = [Diagnostics.Process]::GetCurrentProcess()
$orders = @(
    @('Stored', 'Fastest', 'Optimal'),
    @('Fastest', 'Optimal', 'Stored'),
    @('Optimal', 'Stored', 'Fastest')
)
$extractions = @(try {
    for ($round = 0; $round -lt $orders.Count; $round++) {
        foreach ($mode in $orders[$round]) {
            $root = Join-Path $candidates "extract-$round-$mode"
            $measurement = Measure-Extraction $archives[$mode] $root $process
            [ordered]@{ round = $round + 1; mode = $mode; elapsedMs = $measurement.elapsedMs; cpuMs = $measurement.cpuMs }
        }
    }
} finally { $process.Dispose() })

foreach ($mode in $archives.Keys) {
    $root = Join-Path $candidates "extract-0-$mode"
    $count = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force).Count
    if ($count -ne $entryProof.Count) { throw "Extracted file count mismatch in $mode" }
    foreach ($file in $entryProof) {
        $item = Get-Item -LiteralPath (Join-Path $root $file.path)
        if ($item.Length -ne $file.bytes -or
            (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -cne $file.sha256) {
            throw "Extracted file parity failed in $mode at $($file.path)"
        }
    }
}
$variants = @(foreach ($mode in $archives.Keys) {
    $trials = @($extractions | Where-Object mode -eq $mode)
    [ordered]@{
        mode = $mode
        archiveBytes = (Get-Item -LiteralPath $archives[$mode]).Length
        sha256 = (Get-FileHash -LiteralPath $archives[$mode] -Algorithm SHA256).Hash
        medianElapsedMs = Get-Median $trials.elapsedMs
        medianCpuMs = Get-Median $trials.cpuMs
    }
})
$result = [ordered]@{
    entryCount = $entryProof.Count
    logicalBytes = $logicalBytes
    parity = 'All archive members matched by order, path, length, SHA-256, timestamp and external attributes. All extracted files matched by path, length and SHA-256.'
    method = 'Three rotated extraction orders in one PowerShell process with unique destinations. OS file cache uncontrolled. CPU measures the extracting PowerShell process. Sizes are logical bytes.'
    runtimeCandidate = $RuntimeCandidate
    variants = $variants
    extractions = $extractions
}
$entryProof | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'entries.json')
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'compression.json')
$result | ConvertTo-Json -Depth 6 | Write-Host
