param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use major.minor.patch.' }
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root "dist/Castino-$Version-win-x64"
if (Test-Path $output) { throw "Output already exists: $output. Choose a new version or move the previous build." }
dotnet publish (Join-Path $root 'src/Castino/Castino.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$Version" -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item (Join-Path $root 'README.md') $output
@'
CASTINO - Windows x64

1. Extract the ENTIRE ZIP into a folder.
2. Double-click Castino.exe. No .NET installation is needed.
3. Enable developer options and wireless debugging on Android 11+.
4. Pair your phone using the QR code in Castino, then start mirroring.

Windows 10 version 1809 or newer / Windows 11 (x64).
Keep the tools and Assets folders alongside Castino.exe.
This community build is not digitally signed. Verify its SHA-256 checksum
against SHA256SUMS.txt from the GitHub release.
Documentation: https://github.com/carellice/Castino
'@ | Set-Content (Join-Path $output 'START-HERE.txt') -Encoding utf8
Get-ChildItem $output -Filter *.pdb | Remove-Item
$archive = "$output.zip"
Compress-Archive -Path "$output/*" -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $archive -Leaf)" | Set-Content (Join-Path $root 'dist/SHA256SUMS.txt') -Encoding ascii
Write-Output "Release archive: $archive"
