. (Join-Path $PSScriptRoot 'common.ps1')

$appProject = Join-Path $script:ProjectRoot 'src\DrawAim.App\DrawAim.App.csproj'
$output = Join-Path $script:ProjectRoot 'artifacts\publish\win-x64'

Invoke-DrawAimDotnet publish $appProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    '-p:PublishSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' `
    '-p:DebugType=None' `
    '-p:DebugSymbols=false'

Write-Host "Publish output: $output"
