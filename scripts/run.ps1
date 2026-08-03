. (Join-Path $PSScriptRoot 'common.ps1')

$appProject = Join-Path $script:ProjectRoot 'src\DrawAim.App\DrawAim.App.csproj'
Invoke-DrawAimDotnet run --project $appProject --configuration Release
