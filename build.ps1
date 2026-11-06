#Requires -Version 5.1
<#
.SYNOPSIS
  Build ClangdVS.vsix directly on a physical Windows machine.
  VSCT / CreatePkgDef / VsixUtil run natively via VSSDK.BuildTools.
#>
[CmdletBinding()]
param(
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Debug",

  [string]$Project = "$PSScriptRoot/src/ClangdVS/ClangdVS.csproj"
)

$ErrorActionPreference = "Stop"

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCommand) {
  $dotnet = $dotnetCommand.Source
} elseif ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
  $dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe"
} else {
  throw "dotnet SDK not found. Install the .NET SDK or set DOTNET_ROOT."
}

& $dotnet build $Project -c $Configuration -nologo @args
if ($LASTEXITCODE -ne 0) {
  throw "dotnet build failed with exit code $LASTEXITCODE"
}

$vsix = Join-Path $PSScriptRoot "src/ClangdVS/bin/$Configuration/net472/ClangdVS.vsix"
Write-Host ""
if (Test-Path -LiteralPath $vsix) {
  Write-Host "Built: $vsix"
} else {
  throw "Build succeeded but VSIX not found: $vsix"
}
