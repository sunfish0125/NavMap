[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$pluginProject = Join-Path $repositoryRoot "NavMap.csproj"
$ets2LaProject = Join-Path $repositoryRoot "..\ETS2LA\ETS2LA\ETS2LA.csproj"
$pluginOutput = Join-Path $repositoryRoot "bin\$Configuration\net10.0"
$deployDirectory = Join-Path $repositoryRoot "..\ETS2LA\ETS2LA\bin\$Configuration\net10.0\Plugins"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found. Install .NET 10 first."
}

if (-not (Test-Path -LiteralPath $ets2LaProject -PathType Leaf)) {
    throw "ETS2LA checkout was not found at '$ets2LaProject'."
}

if (-not $SkipBuild) {
    & dotnet build $pluginProject -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "NavMap build failed. No files were deployed."
    }
}

$files = @(
    "NavMap.dll",
    "NavMap.deps.json"
)

if ($IncludeSymbols) {
    $files += "NavMap.pdb"
}

New-Item -ItemType Directory -Force -Path $deployDirectory | Out-Null

foreach ($file in $files) {
    $source = Join-Path $pluginOutput $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required build output was not found: '$source'."
    }

    Copy-Item -LiteralPath $source -Destination $deployDirectory -Force
}

Write-Host "Deployed NavMap to '$deployDirectory'."
Write-Host "Restart ETS2LA to load the new version. Disabling and re-enabling the plugin in Plugin Manager does not pick up the update."
