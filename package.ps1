# Builds the Thunderstore upload zip for Narcotics.
# Uses System.IO.Compression with explicit forward-slash entry paths, because Windows
# PowerShell's Compress-Archive writes backslash entry paths that r2modman mishandles.
# Run AFTER `dotnet build -c Release`. Produces Narcotics-<version>.zip in the repo root.
$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

$manifest = Get-Content (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
$version  = $manifest.version_number
$pkg      = $manifest.name
Write-Host "Packaging $pkg $version"

$dll = Join-Path $root 'bin\Release\Narcotics.dll'
if (-not (Test-Path $dll)) { throw "Build first: $dll not found (run: dotnet build -c Release)" }

$icon = Join-Path $root 'icon.png'
if (-not (Test-Path $icon)) { Write-Warning "icon.png missing - Thunderstore requires a 256x256 icon.png" }

# zip entry path (forward slashes) -> source file on disk
$entries = [ordered]@{
  'manifest.json'                           = Join-Path $root 'manifest.json'
  'README.md'                               = Join-Path $root 'README.md'
  'CHANGELOG.md'                            = Join-Path $root 'CHANGELOG.md'
  'LICENSE'                                 = Join-Path $root 'LICENSE'
  'icon.png'                                = $icon
  'BepInEx/plugins/Narcotics/Narcotics.dll' = $dll
}

$zip = Join-Path $root "$pkg-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
$archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($k in $entries.Keys) {
    $src = $entries[$k]
    if (-not (Test-Path $src)) { Write-Warning "skip (missing): $k"; continue }
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $src, $k) | Out-Null
    Write-Host "  + $k"
  }
} finally {
  $archive.Dispose(); $fs.Dispose()
}
Write-Host "Wrote $zip"
