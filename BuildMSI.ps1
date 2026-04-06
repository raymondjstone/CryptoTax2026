# Build MSI installer for CryptoTax2026
# This script publishes the app and creates an MSI installer using WiX v5

param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

Write-Host "Building CryptoTax2026 MSI Installer..." -ForegroundColor Green

# Step 1: Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "bin\publish\msi") {
    Remove-Item -Recurse -Force "bin\publish\msi"
}
if (Test-Path "bin\installer") {
    Remove-Item -Recurse -Force "bin\installer"
}
if (Test-Path "bin\buildmsi-temp") {
    Remove-Item -Recurse -Force "bin\buildmsi-temp"
}

# Step 2: Build to generate resources.pri (requires MSIX tooling active)
Write-Host "Building to generate resources.pri..." -ForegroundColor Yellow
dotnet build "CryptoTax2026.csproj" -c $Configuration -p:Platform=$Platform

if ($LASTEXITCODE -ne 0) {
    throw "Failed to build application"
}

# Step 3: Publish the application (MSIX tooling disabled to avoid packaging)
Write-Host "Publishing application..." -ForegroundColor Yellow
dotnet publish "CryptoTax2026.csproj" -c $Configuration -r "win-$Platform" --self-contained true -o "bin\publish\msi" -p:GenerateAppxPackageOnBuild=false -p:EnableMsixTooling=false -p:Platform=$Platform

if ($LASTEXITCODE -ne 0) {
    throw "Failed to publish application"
}

# Step 2b: Copy resources.pri into the publish output.
# dotnet publish does not include resources.pri for WinUI 3 apps (it is normally
# bundled by MSIX packaging). For unpackaged/MSI deployment we must copy it manually.
# Check both possible locations - the path varies depending on build state.
$priCandidates = @(
    "bin\$Platform\$Configuration\net8.0-windows10.0.19041.0\resources.pri",
    "bin\$Platform\$Configuration\net8.0-windows10.0.19041.0\win-$Platform\resources.pri"
)
$resourcesPri = $priCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($resourcesPri) {
    Copy-Item $resourcesPri "bin\publish\msi\resources.pri" -Force
    Write-Host "Copied resources.pri from $resourcesPri" -ForegroundColor Cyan
} else {
    Write-Warning "resources.pri not found in any expected location"
}

# Step 3: Generate file list for WiX
Write-Host "Generating file list for installer..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path "bin\installer" | Out-Null

# Build the harvested WiX XML with proper subdirectory support
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="HarvestedFiles" Directory="INSTALLFOLDER">')

$publishRoot = (Get-Item "bin\publish\msi").FullName
$fileCount = 0
Get-ChildItem -Path "bin\publish\msi" -File -Recurse | ForEach-Object {
    $relativePath = $_.FullName.Substring($publishRoot.Length).TrimStart('\')
    $relativePathForward = $relativePath.Replace('\', '/')
    $fileName = $_.Name
    $componentId = "File_$fileCount"

    $cleanFileName = $fileName -replace '[^A-Za-z0-9_.]', '_' -replace '__+', '_'
    if ($cleanFileName.Length -gt 50) {
        $cleanFileName = $cleanFileName.Substring(0, 50)
    }
    $fileId = "File_${fileCount}_$cleanFileName"

    $keyPath = ''
    if ($fileName -eq "CryptoTax2026.exe") { $keyPath = ' KeyPath="yes"' }

    # Preserve subdirectory structure in the MSI
    $subDir = Split-Path $relativePath -Parent
    $subDirAttr = ''
    if ($subDir) {
        $subDirAttr = " Subdirectory=`"$subDir`""
    }

    [void]$sb.AppendLine("      <Component Id=`"$componentId`" Guid=`"*`"$subDirAttr>")
    [void]$sb.AppendLine("        <File Id=`"$fileId`" Source=`"bin\publish\msi\$relativePathForward`"$keyPath />")
    [void]$sb.AppendLine("      </Component>")
    $fileCount++
}

[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

New-Item -ItemType Directory -Force -Path "bin\buildmsi-temp" | Out-Null
$sb.ToString() | Out-File -FilePath "bin\buildmsi-temp\HarvestedFiles.wxs" -Encoding UTF8

# Step 4: Build the MSI
Write-Host "Building MSI package..." -ForegroundColor Yellow
$outputName = "CryptoTax2026-$Platform.msi"
[xml]$manifest = Get-Content "Package.appxmanifest"
$productVersion = $manifest.Package.Identity.Version
Write-Host "Product version: $productVersion" -ForegroundColor Cyan
$utilCaMap = @{ "x64" = "Wix4UtilCA_X64"; "x86" = "Wix4UtilCA_X86"; "arm64" = "Wix4UtilCA_A64" }
$utilCa = $utilCaMap[$Platform.ToLower()]
wix build "Installer.wxs" "bin\buildmsi-temp\HarvestedFiles.wxs" -o "bin\installer\$outputName" -arch $Platform.ToLower() -d "ProductVersion=$productVersion" -d "SourceDir=bin\publish\msi" -d "UtilCA=$utilCa" -ext WixToolset.Util.wixext -pdbtype none

if ($LASTEXITCODE -ne 0) {
    throw "Failed to build MSI"
}

Write-Host "MSI installer created successfully at bin\installer\$outputName" -ForegroundColor Green
Write-Host "Installer size: $((Get-Item "bin\installer\$outputName").Length / 1MB) MB" -ForegroundColor Cyan
