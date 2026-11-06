param([switch]$SkipExtensionBuild)
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '../..'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCommand) {
    $dotnet = $dotnetCommand.Source
} elseif ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
} else {
    throw 'dotnet SDK not found. Install the .NET SDK or set DOTNET_ROOT.'
}
if (-not $SkipExtensionBuild) {
    & $dotnet build (Join-Path $root 'src/ClangdVS/ClangdVS.csproj') -c Release -nologo -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Extension build failed.' }
}
& $dotnet build (Join-Path $PSScriptRoot 'RegressionHarness.csproj') -nologo -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }
& (Join-Path $PSScriptRoot 'bin/Debug/net472/RegressionHarness.exe')
if ($LASTEXITCODE -ne 0) { throw 'Behavior regressions failed.' }
