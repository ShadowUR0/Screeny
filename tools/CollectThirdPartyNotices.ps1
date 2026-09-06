# Modified in the ShadowUR0 Screeny fork in 2026.
param(
    [string]$AssetsFile = "obj/project.assets.json",
    [string]$OutputDirectory = "artifacts/third-party"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $AssetsFile)) {
    throw "NuGet assets file not found: $AssetsFile. Run dotnet restore first."
}

$assets = Get-Content $AssetsFile -Raw | ConvertFrom-Json
$packageRoot = ($assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($packageRoot)) {
    throw "Could not determine the NuGet global-packages directory."
}

Remove-Item $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$manifest = New-Object System.Collections.Generic.List[string]
$manifest.Add("Screeny third-party package licenses")
$manifest.Add("Generated from the resolved NuGet dependency graph.")
$manifest.Add("")
$packages = $assets.libraries.PSObject.Properties |
    Where-Object { $_.Value.type -eq "package" } |
    Sort-Object Name

foreach ($package in $packages) {
    $parts = $package.Name -split "/", 2
    if ($parts.Count -ne 2) { continue }

    $id = $parts[0]
    $version = $parts[1]
    $packageDirectory = Join-Path (Join-Path $packageRoot $id.ToLowerInvariant()) $version.ToLowerInvariant()
    $nuspec = Get-ChildItem $packageDirectory -Filter "*.nuspec" -File -ErrorAction SilentlyContinue | Select-Object -First 1

    $licenseText = "Unknown"
    if ($nuspec) {
        [xml]$xml = Get-Content $nuspec.FullName
        $metadata = $xml.package.metadata
        if ($metadata.license) {
            $licenseValue = $metadata.license.'#text'
            if (!$licenseValue) { $licenseValue = $metadata.license.InnerText }
            if ($licenseValue) { $licenseText = $licenseValue.Trim() }
        } elseif ($metadata.licenseUrl) {
            $licenseText = $metadata.licenseUrl.Trim()
        }
    }

    $manifest.Add("$id $version | $licenseText")

    if (!(Test-Path $packageDirectory)) { continue }

    $noticeFiles = Get-ChildItem $packageDirectory -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '(?i)^(license|notice|third-party-notices)' } |
        Sort-Object FullName -Unique

    foreach ($notice in $noticeFiles) {
        $relative = $notice.FullName.Substring($packageDirectory.Length).TrimStart('\', '/')
        $safeRelative = $relative -replace '[\\/:*?"<>|]', '_'
        $destinationName = "$id-$version-$safeRelative"
        Copy-Item $notice.FullName (Join-Path $OutputDirectory $destinationName) -Force
    }
}

$manifestPath = Join-Path $OutputDirectory "PACKAGE-LICENSES.txt"
$manifest | Set-Content $manifestPath -Encoding UTF8

Write-Output "Collected license metadata for $($packages.Count) resolved NuGet packages."
Write-Output "Output: $OutputDirectory"
