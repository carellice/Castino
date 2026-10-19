param([Parameter(Mandatory=$true)][string]$Manifest, [switch]$NoRestart)
$ErrorActionPreference = 'Stop'
$changed = [System.Collections.Generic.List[object]]::new()
$settings = $null
$rollbackOk = $true

function Get-Sha256([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $hash.Dispose(); $stream.Dispose() }
}

function Assert-NoLinks([string]$path) {
    $current = $path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "La cartella contiene un collegamento: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Save-Result([bool]$success, [string]$message) {
    if ($settings -and $settings.ResultFile) {
        @{ Success = $success; Message = $message; Version = $settings.Version } |
            ConvertTo-Json | Set-Content -LiteralPath $settings.ResultFile -Encoding UTF8
    }
}

try {
    $manifestPath = [IO.Path]::GetFullPath($Manifest)
    $work = [IO.Path]::GetDirectoryName($manifestPath)
    $settings = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $target = [IO.Path]::GetFullPath($settings.Target).TrimEnd('\')
    $source = [IO.Path]::GetFullPath((Join-Path $work 'package'))
    $backup = Join-Path $work 'backup'
    if ($target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\') -or
        $source.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath (Join-Path $target 'Castino.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $source 'Castino.exe'))) {
        throw 'Cartella di aggiornamento non valida.'
    }
    Assert-NoLinks $source
    Assert-NoLinks $target
    # Resolve every destination before any write to the installation.
    $files = @(Get-ChildItem -LiteralPath $source -File -Recurse -Force)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $destination = [IO.Path]::GetFullPath((Join-Path $target $relative))
        if (-not $destination.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'File fuori dalla cartella di Castino.'
        }
        Assert-NoLinks $destination
    }
    'ready' | Set-Content -LiteralPath (Join-Path $work 'ready') -Encoding ASCII
    $running = Get-Process -Id $settings.ProcessId -ErrorAction SilentlyContinue
    if ($running) {
        if ($running.Path -ne (Join-Path $target 'Castino.exe')) { throw 'Il processo da attendere non e Castino.' }
        $running | Wait-Process -Timeout 90
    }
    New-Item -ItemType Directory -Path $backup | Out-Null
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $destination = Join-Path $target $relative
        Assert-NoLinks $destination
        $existed = Test-Path -LiteralPath $destination
        if ($existed -and (Get-Sha256 $destination) -eq (Get-Sha256 $file.FullName)) { continue }
        $saved = Join-Path $backup $relative
        if ($existed) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)) | Out-Null
            [IO.File]::Copy($destination, $saved, $false)
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        $changed.Add(@{ Path = $destination; Backup = $saved; Existed = $existed })
        [IO.File]::Copy($file.FullName, $destination, $true)
    }
    Save-Result $true 'Aggiornamento installato correttamente.'
    if (-not $NoRestart) { Start-Process -FilePath (Join-Path $target 'Castino.exe') -WorkingDirectory $target }
    # The backup is retained in the temporary work folder for recovery.
    exit 0
}
catch {
    $failure = $_.Exception.Message
    for ($i = $changed.Count - 1; $i -ge 0; $i--) {
        $item = $changed[$i]
        try {
            if ($item.Existed) {
                $alreadyRestored = (Test-Path -LiteralPath $item.Path) -and
                    ((Get-Sha256 $item.Path) -eq (Get-Sha256 $item.Backup))
                if (-not $alreadyRestored) { [IO.File]::Copy($item.Backup, $item.Path, $true) }
            }
            elseif (Test-Path -LiteralPath $item.Path) { Remove-Item -LiteralPath $item.Path -Force }
        }
        catch { $rollbackOk = $false }
    }
    $message = "Aggiornamento non riuscito: $failure"
    if ($rollbackOk) { $message += ' La versione precedente e stata mantenuta.' }
    else { $message += " Ripristino incompleto. Backup: $backup" }
    try { Save-Result $false $message } catch { }
    try { $message | Set-Content -LiteralPath (Join-Path $work 'update-error.txt') -Encoding UTF8 } catch { }
    if (-not $NoRestart) {
        try {
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show($message, 'Castino - aggiornamento') | Out-Null
            if ($rollbackOk -and $target) { Start-Process -FilePath (Join-Path $target 'Castino.exe') -WorkingDirectory $target }
        }
        catch { }
    }
    exit 1
}
