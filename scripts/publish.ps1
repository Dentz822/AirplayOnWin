# Builds dist\WindowsAirplay.exe: one self-contained file (no .NET install needed), Windows 10/11 x64.
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

dotnet publish (Join-Path $root 'src\AirPlay.App\AirPlay.App.csproj') -c Release -p:PublishSingleFile=true -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Remove-Item (Join-Path $dist '*.pdb') -ErrorAction SilentlyContinue  # debug symbols of referenced projects

$exe = Join-Path $dist 'WindowsAirplay.exe'
Write-Host ("Built {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
