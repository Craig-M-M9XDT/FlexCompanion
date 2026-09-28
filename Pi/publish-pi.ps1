$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot "publish/pi-arm64"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet publish .\FlexCompanion.Pi.csproj `
  -c Release `
  -r linux-arm64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -o $out

Write-Host "Pi ARM64 build created at: $out"
Write-Host "Copy the whole folder to the Raspberry Pi, or run deploy/install-pi.sh on the Pi."
