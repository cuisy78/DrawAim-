param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

. (Join-Path $PSScriptRoot 'common.ps1')

$solution = Join-Path $script:ProjectRoot 'DrawAim.slnx'
Invoke-DrawAimDotnet restore $solution --ignore-failed-sources
Invoke-DrawAimDotnet build $solution --configuration $Configuration --no-restore
