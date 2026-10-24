param([string]$PublishDirectory = "$PSScriptRoot\..\artifacts\win-x64")
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$artifactRoot = Join-Path $projectRoot 'artifacts'
if (!(Test-Path -LiteralPath (Join-Path $publishRoot 'PeripheralPower.exe'))) { throw '请先执行 build.ps1 -Publish 生成程序。' }

foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publishRoot -Force
}
$publishedDocs = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Path $publishedDocs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File | Copy-Item -Destination $publishedDocs -Force
$binaryZip = Join-Path $artifactRoot 'PeripheralPower-win-x64.zip'
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $binaryZip -Force

# Include source explicitly; never bundle local SDKs, credentials, binaries, or user profiles.
$sourceZip = Join-Path $artifactRoot 'PeripheralPower-source.zip'
$stream = [IO.File]::Open($sourceZip, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    $sourceFiles = @()
    foreach ($folder in @('src', 'tests', 'scripts', 'docs')) {
        $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    }
    foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'PeripheralPower.sln', 'Directory.Build.props', 'global.json', 'NuGet.Config', '.gitignore')) {
        $sourceFiles += Get-Item -LiteralPath (Join-Path $projectRoot $name)
    }
    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose(); $stream.Dispose() }

$packageFiles = @($binaryZip, $sourceZip, (Join-Path $publishRoot 'PeripheralPower.exe'))
# Optional local test evidence; keep original relative paths so the reports are traceable.
$evidenceFiles = @(
    'artifacts/test-core.txt',
    'artifacts/build-1.1.log',
    'tests/PeripheralPower.NativeTests/results-native.json',
    'artifacts/ui-release/results.json',
    'artifacts/ui-release-1.1/results.json',
    'artifacts/performance-results.json',
    'artifacts/performance-results.samples.json',
    'artifacts/performance-analysis.json',
    'artifacts/performance-timeout-state.json',
    'artifacts/tray-icons-preview.png',
    'artifacts/zen65-identity-current.json',
    'artifacts/zen65-identity-unplugged.json',
    'artifacts/zen65-wired-read-result.json',
    'artifacts/rog-read-result.json',
    'artifacts/actual-discovery.json'
) | ForEach-Object { Join-Path $projectRoot $_ } | Where-Object { Test-Path -LiteralPath $_ }
foreach ($uiFolder in @('ui-release', 'ui-release-1.1')) {
    $uiEvidence = Join-Path $artifactRoot $uiFolder
    if (Test-Path -LiteralPath $uiEvidence) {
        $evidenceFiles += Get-ChildItem -LiteralPath $uiEvidence -Filter '*.png' -File | ForEach-Object { $_.FullName }
    }
}
if ($evidenceFiles.Count -gt 0) {
    $evidenceZip = Join-Path $artifactRoot 'PeripheralPower-test-evidence.zip'
    $evidenceStream = [IO.File]::Open($evidenceZip, [IO.FileMode]::Create)
    $evidenceArchive = [IO.Compression.ZipArchive]::new($evidenceStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in $evidenceFiles) {
            $relative = [IO.Path]::GetRelativePath($projectRoot, $file).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($evidenceArchive, $file, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $evidenceArchive.Dispose(); $evidenceStream.Dispose() }
    $packageFiles += $evidenceZip
}

$checksums = foreach ($file in $packageFiles) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetRelativePath($artifactRoot, $file).Replace('\', '/')
}
$checksums | Set-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding ascii
Get-Item -LiteralPath $packageFiles | Select-Object Name, Length
