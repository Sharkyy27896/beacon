$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

# Use the local toolchain when present, otherwise whatever dotnet is on PATH.
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$publishDir = Join-Path $root 'publish\Beacon'
$payloadDir = Join-Path $root 'src\Beacon.Installer\Payload'
$payloadZip = Join-Path $payloadDir 'BeaconPayload.zip'
$installerOut = Join-Path $root 'publish\Installer'
$finalExe = Join-Path $root 'Beacon-Setup.exe'

# 1. Publish the app. Its assembly metadata is the version the installer reports.
& $dotnet publish (Join-Path $root 'src\Beacon\Beacon.csproj') `
    -c Release -r win-x64 --self-contained true -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# 2. Pack the published app into the payload embedded in the setup EXE.
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $payloadZip -CompressionLevel Optimal
Write-Host "Payload: $payloadZip ($([math]::Round((Get-Item $payloadZip).Length / 1MB, 1)) MB)"

# 3. Build the single-file themed installer.
& $dotnet publish (Join-Path $root 'src\Beacon.Installer\Beacon.Installer.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $installerOut --nologo
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $installerOut 'Beacon.Setup.exe') $finalExe -Force
Get-Item $finalExe | Select-Object FullName, Length
