<#
.SYNOPSIS
    Builds a WinIsland release locally: a self-contained publish, a portable zip,
    and the Inno Setup installer. You then upload the artifacts to a GitHub
    Release yourself.

.DESCRIPTION
    Mirrors what the old GitHub Action did, but runs on your machine. The version
    you pass is stamped into the assembly (so the in-app updater can compare it)
    and into the installer/file names.

.PARAMETER Version
    The release version, e.g. 1.0.1 (no leading "v").

.EXAMPLE
    ./build-release.ps1 1.0.1
    # Produces dist\WinIsland-1.0.1-Setup.exe and dist\WinIsland-1.0.1-portable.zip
    # Then create a GitHub Release tagged v1.0.1 and upload the Setup.exe.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$publish = Join-Path $root "publish\WinIsland"
$dist    = Join-Path $root "dist"

Write-Host "Building WinIsland $Version" -ForegroundColor Cyan

# 1. Clean previous publish output and ensure dist exists.
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# 2. Publish (self-contained, single folder) with the version stamped in.
dotnet publish "$root\WinIsland\WinIsland.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 3. Portable zip.
$zip = Join-Path $dist "WinIsland-$Version-portable.zip"
Compress-Archive -Path "$publish\*" -DestinationPath $zip -Force
Write-Host "Created $zip" -ForegroundColor Green

# 4. Installer (Inno Setup). The .iss reads APP_VERSION and BUILD_DIR from env.
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    throw "Inno Setup not found at '$iscc'. Install it from https://jrsoftware.org/isdl.php"
}
$env:APP_VERSION = $Version
$env:BUILD_DIR   = $publish
& $iscc "$root\installer\WinIsland.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup build failed" }

$setup = Join-Path $dist "WinIsland-$Version-Setup.exe"
Write-Host ""
Write-Host "Done. Artifacts in dist\:" -ForegroundColor Green
Write-Host "  $setup"
Write-Host "  $zip"
Write-Host ""
Write-Host "Next: create a GitHub Release with tag v$Version and upload the Setup.exe." -ForegroundColor Yellow
