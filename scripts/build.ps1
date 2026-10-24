param([switch]$Publish, [switch]$NativeTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
Push-Location $projectRoot
try {
    & $sdk build src/PeripheralPower.App/PeripheralPower.App.csproj -c Release --configfile NuGet.Config
    if ($LASTEXITCODE) { throw '构建失败' }
    & $sdk build tests/PeripheralPower.Tests -c Release --configfile NuGet.Config
    if ($LASTEXITCODE) { throw 'Core 测试构建失败' }
    & $sdk run --project tests/PeripheralPower.Tests -c Release --no-build --no-restore
    if ($LASTEXITCODE) { throw 'Core 测试失败' }
    if ($NativeTests) {
        & $sdk build tests/PeripheralPower.NativeTests -c Release --configfile NuGet.Config
        if ($LASTEXITCODE) { throw 'Native 测试构建失败' }
        & $sdk run --project tests/PeripheralPower.NativeTests -c Release --no-build --no-restore
        if ($LASTEXITCODE) { throw 'Native 测试失败' }
    }
    if ($Publish) {
        & $sdk publish src/PeripheralPower.App/PeripheralPower.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64 --configfile NuGet.Config
        if ($LASTEXITCODE) { throw '发布失败' }
        & (Join-Path $PSScriptRoot 'package.ps1')
    }
} finally { Pop-Location }
