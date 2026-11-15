param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'

function Get-EntryHash([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}

function Test-PayloadParity([IO.Compression.ZipArchive]$Original, [IO.Compression.ZipArchive]$Candidate) {
    if ($Candidate.Entries.Count -ne $Original.Entries.Count) { throw 'Portable ZIP entry count changed' }
    for ($index = 0; $index -lt $Original.Entries.Count; $index++) {
        $before = $Original.Entries[$index]
        $after = $Candidate.Entries[$index]
        if ($after.FullName -cne $before.FullName -or $after.Length -ne $before.Length -or
            $after.LastWriteTime -ne $before.LastWriteTime -or $after.ExternalAttributes -ne $before.ExternalAttributes -or
            (Get-EntryHash $after) -cne (Get-EntryHash $before)) {
            throw "Portable ZIP entry changed: $($before.FullName)"
        }
    }
}

$package = (Get-Item -LiteralPath $PackageDirectory).FullName
$installers = @(Get-ChildItem -LiteralPath $package -File -Filter 'Snipit-*-x64-Setup.exe')
$archives = @(Get-ChildItem -LiteralPath $package -File -Filter 'Snipit-*-x64-Portable.zip')
if ($installers.Count -ne 1 -or $archives.Count -ne 1) { throw 'Expected one Snipit installer and one portable ZIP' }
$installer = $installers[0]
$archive = $archives[0]
$manifestPath = Join-Path $package 'SHA256SUMS.txt'
$manifest = @(Get-Content -LiteralPath $manifestPath)
$installerLine = "$((Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash)  $($installer.Name)"
$archiveLine = "$((Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash)  $($archive.Name)"
if ($manifest.Count -ne 2 -or $manifest -notcontains $installerLine -or $manifest -notcontains $archiveLine) {
    throw 'SeanPak artifact hashes do not match SHA256SUMS.txt'
}

$tempPath = Join-Path $package ".portable-$([guid]::NewGuid().ToString('N'))"
$newArchive = "$tempPath.zip"
$newManifest = "$tempPath.sha256"
$backup = "$tempPath.bak"
try {
    $source = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        $destination = [IO.Compression.ZipFile]::Open($newArchive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $source.Entries) {
                $copy = $destination.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                $copy.LastWriteTime = $entry.LastWriteTime
                $copy.ExternalAttributes = $entry.ExternalAttributes
                $inputStream = $entry.Open()
                try {
                    $outputStream = $copy.Open()
                    try { $inputStream.CopyTo($outputStream) }
                    finally { $outputStream.Dispose() }
                } finally { $inputStream.Dispose() }
            }
        } finally { $destination.Dispose() }

        $candidate = [IO.Compression.ZipFile]::OpenRead($newArchive)
        try { Test-PayloadParity $source $candidate }
        finally { $candidate.Dispose() }
    } finally { $source.Dispose() }

    $compressedBytes = (Get-Item -LiteralPath $newArchive).Length
    if ($compressedBytes -ge $archive.Length) {
        Write-Host "Portable ZIP already has the smaller size ($($archive.Length) bytes)."
        return
    }

    $newHash = (Get-FileHash -LiteralPath $newArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest | ForEach-Object { if ($_ -ieq $archiveLine) { "$newHash  $($archive.Name)" } else { $_ } } |
        Set-Content -LiteralPath $newManifest -Encoding ascii

    [IO.File]::Replace($newArchive, $archive.FullName, $backup)
    try { [IO.File]::Move($newManifest, $manifestPath, $true) }
    catch {
        [IO.File]::Move($backup, $archive.FullName, $true)
        throw
    }
    [IO.File]::Delete($backup)
    Write-Host "Portable ZIP compressed from $($archive.Length) to $compressedBytes bytes. Every entry passed SHA-256 and metadata checks."
} finally {
    [IO.File]::Delete($newArchive)
    [IO.File]::Delete($newManifest)
}
