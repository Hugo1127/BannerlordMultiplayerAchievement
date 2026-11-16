<#
.SYNOPSIS
    Build the ChargeTweaks dedicated-server module.

.DESCRIPTION
    The local machine usually has a newer .NET SDK but no net8.0 targeting pack,
    so this script compiles directly with Roslyn csc against the .NET 8 shared
    runtime assemblies plus the TaleWorlds*.dll files from the server bin folder.

    Output: ChargeTweaks\bin\Win64_Shipping_Server\ChargeTweaks.dll (inside this folder)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
param(
    [string]$ServerBin = "",
    [string]$DotnetRoot = "C:\Program Files\dotnet",
    [string]$DotnetRuntimeVersion = ""
)

$ErrorActionPreference = "Stop"

$modRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $modRoot "src"
$outDir = Join-Path $modRoot "ChargeTweaks\bin\Win64_Shipping_Server"

if ([string]::IsNullOrEmpty($ServerBin)) {
    $ServerBin = Join-Path (Split-Path -Parent $modRoot) "bin\Win64_Shipping_Server"
}

if (-not (Test-Path $ServerBin)) {
    throw "Server bin directory not found: $ServerBin (use -ServerBin to override)"
}

# --- locate csc ---
$csc = Get-ChildItem (Join-Path $DotnetRoot "sdk") -Directory |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName "Roslyn\bincore\csc.dll" } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

if (-not $csc) {
    throw "Roslyn csc.dll not found under $DotnetRoot (use -DotnetRoot to override)"
}

# --- locate the .NET 8 shared runtime (used as reference assemblies) ---
$runtimeRoot = Join-Path $DotnetRoot "shared\Microsoft.NETCore.App"
$runtimeDirs = Get-ChildItem $runtimeRoot -Directory | Where-Object { $_.Name -like "8.0.*" }
if ($DotnetRuntimeVersion) {
    $runtimeDirs = $runtimeDirs | Where-Object { $_.Name -eq $DotnetRuntimeVersion }
}
$runtimeDir = $runtimeDirs | Sort-Object Name -Descending | Select-Object -First 1
if (-not $runtimeDir) {
    throw "No .NET 8 runtime found under $runtimeRoot (use -DotnetRuntimeVersion to override)"
}

# --- collect references ---
$refs = New-Object System.Collections.Generic.List[string]
Get-ChildItem $runtimeDir.FullName -Filter *.dll |
    Where-Object { $_.Name -notlike "*Native*" -and ($_.Name -like "System.*.dll" -or $_.Name -in @("mscorlib.dll", "netstandard.dll", "System.dll", "System.Core.dll")) } |
    ForEach-Object { $refs.Add("-r:`"$($_.FullName)`"") }

Get-ChildItem $ServerBin -Filter "TaleWorlds*.dll" |
    ForEach-Object { $refs.Add("-r:`"$($_.FullName)`"") }

if ($refs.Count -lt 10) {
    throw "Unexpected reference count ($($refs.Count)); check the .NET runtime and server folders"
}

# --- compile ---
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$dll = Join-Path $outDir "ChargeTweaks.dll"
$sources = @(Get-ChildItem $srcDir -Filter *.cs | ForEach-Object { $_.FullName })

$cscArgs = @($csc, "-nologo", "-target:library", "-optimize+", "-nostdlib", "-out:`"$dll`"") + $refs + $sources

Write-Host "csc        : $csc"
Write-Host "runtime    : $($runtimeDir.Name)"
Write-Host "server bin : $ServerBin"
Write-Host "sources    : $($sources.Count)"
Write-Host "output     : $dll"

& dotnet @cscArgs
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$info = Get-Item $dll
Write-Host ""
Write-Host "OK: $($info.FullName) ($($info.Length) bytes)"
