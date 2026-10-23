<#
.SYNOPSIS
    Builds the portable, self-contained Windows executable.

.EXAMPLE
    ./build-windows.ps1
    ./build-windows.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$version = (Get-Content (Join-Path $projectDir "version.json") -Raw | ConvertFrom-Json).version
$outputDir = Join-Path $projectDir "artifacts/windows/$Runtime"

if (Test-Path $outputDir) {
    Remove-Item -Recurse -Force $outputDir
}

dotnet publish (Join-Path $projectDir "PremiumYoutubeDownloader.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $outputDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $outputDir "PremiumYoutubeDownloader.exe"
$versionedExe = Join-Path $outputDir "PremiumYoutubeDownloader-$version-$Runtime.exe"
Copy-Item $exe $versionedExe -Force

Write-Host $versionedExe
