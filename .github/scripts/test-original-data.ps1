[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $DataRoot,

    [string] $ManifestPath = (Join-Path $PSScriptRoot 'original-data.manifest.json')
)

$ErrorActionPreference = 'Stop'
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)

if (-not (Test-Path -LiteralPath $DataRoot -PathType Container)) {
    throw "Original Diggles data directory was not found at '$DataRoot'."
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Original-data manifest was not found at '$ManifestPath'."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported original-data manifest schema '$($manifest.schemaVersion)'."
}

foreach ($relativeDirectory in $manifest.requiredDirectories) {
    $directoryPath = Join-Path $DataRoot $relativeDirectory
    if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
        throw "Required original-data directory '$relativeDirectory' was not found at '$directoryPath'."
    }
}

foreach ($expectedFile in $manifest.files) {
    $relativePath = [string] $expectedFile.path
    $filePath = Join-Path $DataRoot $relativePath
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "Required original-data file '$relativePath' was not found at '$filePath'."
    }

    $actualFile = Get-Item -LiteralPath $filePath
    if ($actualFile.Length -ne [long] $expectedFile.size) {
        throw "Original-data file '$relativePath' has size $($actualFile.Length); expected $($expectedFile.size)."
    }

    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string] $expectedFile.sha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Original-data file '$relativePath' has SHA-256 '$actualHash'; expected '$expectedHash'."
    }
}

Write-Host ("Original data verified: release={0}, locale={1}, files={2}, root={3}" -f `
    $manifest.release.id, $manifest.release.locale, $manifest.files.Count, $DataRoot)
