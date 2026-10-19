$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sandbox = Join-Path $root ("tmp/installer-tests-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($sandbox) | Out-Null
$helper = Join-Path $root 'src/Castino/Updates/Install-Update.ps1'

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    Write-Output "PASS: $message"
}
function Scenario([string]$name) {
    $work = Join-Path $sandbox $name
    $target = Join-Path $work "Castino [test] e l'aggiornamento"
    $package = Join-Path $work 'package'
    [IO.Directory]::CreateDirectory($target) | Out-Null
    [IO.Directory]::CreateDirectory($package) | Out-Null
    [IO.File]::WriteAllText((Join-Path $target 'Castino.exe'), 'old executable')
    [IO.File]::WriteAllText((Join-Path $package 'Castino.exe'), 'new executable')
    $manifest = Join-Path $work 'install.json'
    $result = Join-Path $work 'result.json'
    @{Target=$target;ProcessId=99999999;ResultFile=$result;Version='0.1.2'} |
        ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding UTF8
    return @{Work=$work;Target=$target;Package=$package;Manifest=$manifest;Result=$result}
}

$success = Scenario 'success'
[IO.File]::WriteAllText((Join-Path $success.Package 'new.txt'), 'new file')
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Manifest $success.Manifest -NoRestart
Assert ($LASTEXITCODE -eq 0) 'installer succeeds with spaces, brackets and apostrophes in paths'
Assert (([IO.File]::ReadAllText((Join-Path $success.Target 'Castino.exe'))) -eq 'new executable') 'executable replaced'
Assert (([IO.File]::ReadAllText((Join-Path $success.Work 'backup/Castino.exe'))) -eq 'old executable') 'old executable backed up'
Assert (Test-Path -LiteralPath (Join-Path $success.Target 'new.txt')) 'new files installed'
Assert ((Get-Content -LiteralPath $success.Result -Raw | ConvertFrom-Json).Success) 'success recorded'

$failure = Scenario 'locked-file'
[IO.File]::WriteAllText((Join-Path $failure.Target 'z-locked.dll'), 'old library')
[IO.File]::WriteAllText((Join-Path $failure.Package 'z-locked.dll'), 'new library')
[IO.File]::WriteAllText((Join-Path $failure.Package 'a-new.txt'), 'new file')
$locked = [IO.File]::Open((Join-Path $failure.Target 'z-locked.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Manifest $failure.Manifest -NoRestart
    Assert ($LASTEXITCODE -eq 1) 'locked file causes controlled failure'
}
finally { $locked.Dispose() }
Assert (([IO.File]::ReadAllText((Join-Path $failure.Target 'Castino.exe'))) -eq 'old executable') 'previous executable restored after partial update'
Assert (-not (Test-Path -LiteralPath (Join-Path $failure.Target 'a-new.txt'))) 'new files removed on rollback'
Assert (([IO.File]::ReadAllText((Join-Path $failure.Target 'z-locked.dll'))) -eq 'old library') 'locked original unchanged'
$result = Get-Content -LiteralPath $failure.Result -Raw | ConvertFrom-Json
Assert (-not $result.Success -and $result.Message.Contains('mantenuta')) 'rollback result recorded'

$unchanged = Scenario 'unchanged-file'
[IO.File]::WriteAllText((Join-Path $unchanged.Target 'adb.exe'), 'same library')
[IO.File]::WriteAllText((Join-Path $unchanged.Package 'adb.exe'), 'same library')
$locked = [IO.File]::Open((Join-Path $unchanged.Target 'adb.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Manifest $unchanged.Manifest -NoRestart
    Assert ($LASTEXITCODE -eq 0) 'unchanged locked dependencies are skipped'
}
finally { $locked.Dispose() }
Write-Output "Installer tests passed. Test files: $sandbox"
