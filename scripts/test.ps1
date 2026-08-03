. (Join-Path $PSScriptRoot 'common.ps1')

$testProject = Join-Path $script:ProjectRoot 'tests\DrawAim.Tests\DrawAim.Tests.csproj'
Invoke-DrawAimDotnet run --project $testProject --configuration Release
