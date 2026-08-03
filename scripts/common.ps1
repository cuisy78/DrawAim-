$ErrorActionPreference = 'Stop'

$script:ProjectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $script:ProjectRoot '.dotnet-sdk\dotnet.exe'

if (Test-Path -LiteralPath $localDotnet) {
    $script:DotnetExe = $localDotnet
}
else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw 'A .NET SDK allowed by global.json (10.0.302 or a higher 10.0.3xx patch) was not found. See README.md.'
    }

    $script:DotnetExe = $command.Source
}

$env:DOTNET_CLI_HOME = Join-Path $script:ProjectRoot '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $script:ProjectRoot '.nuget\packages'

function Invoke-DrawAimDotnet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & $script:DotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}
