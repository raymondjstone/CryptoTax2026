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

# Step 2: Publish the application
Write-Host "Publishing application..." -ForegroundColor Yellow
dotnet publish "CryptoTax2026.csproj" -c $Configuration -r "win-$Platform" --self-contained true -o "bin\publish\msi" -p:GenerateAppxPackageOnBuild=false -p:EnableMsixTooling=false -p:Platform=$Platform

if ($LASTEXITCODE -ne 0) {
    throw "Failed to publish application"
}

# Step 3: Generate file list for WiX
Write-Host "Generating file list for installer..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path "bin\installer" | Out-Null

# Create a proper harvested files list with individual components
$harvestedXml = @"
<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <ComponentGroup Id="HarvestedFiles" Directory="INSTALLFOLDER">
"@

# Add components for each file
$fileCount = 0
Get-ChildItem -Path "bin\publish\msi" -File -Recurse | ForEach-Object {
    $relativePath = $_.FullName.Replace((Get-Item "bin\publish\msi").FullName, "").TrimStart('\').Replace('\', '/')
    $fileName = $_.Name
    $componentId = "File_$fileCount"

    # Create a valid WiX ID by removing invalid characters and limiting length
    $cleanFileName = $fileName -replace '[^A-Za-z0-9_.]', '_' -replace '__+', '_'
    if ($cleanFileName.Length -gt 50) {
        $cleanFileName = $cleanFileName.Substring(0, 50)
    }
    $fileId = "File_${fileCount}_$cleanFileName"

    $keyPath = if ($fileName -eq "CryptoTax2026.exe") { ' KeyPath="yes"' } else { '' }

    $harvestedXml += @"
      <Component Id="$componentId" Guid="*">
        <File Id="$fileId" Source="bin\publish\msi\$relativePath"$keyPath />
      </Component>
"@
    $fileCount++
}

$harvestedXml += @"
    </ComponentGroup>
  </Fragment>
</Wix>
"@

New-Item -ItemType Directory -Force -Path "bin\buildmsi-temp" | Out-Null
$harvestedXml | Out-File -FilePath "bin\buildmsi-temp\HarvestedFiles.wxs" -Encoding UTF8

if ($LASTEXITCODE -ne 0) {
    throw "Failed to harvest files"
}

# Step 4: Build the MSI
Write-Host "Building MSI package..." -ForegroundColor Yellow
$outputName = "CryptoTax2026-$Platform.msi"
[xml]$manifest = Get-Content "Package.appxmanifest"
$productVersion = $manifest.Package.Identity.Version
Write-Host "Product version: $productVersion" -ForegroundColor Cyan
wix build "Installer.wxs" "bin\buildmsi-temp\HarvestedFiles.wxs" -o "bin\installer\$outputName" -arch $Platform.ToLower() -d "ProductVersion=$productVersion"

if ($LASTEXITCODE -ne 0) {
    throw "Failed to build MSI"
}

Write-Host "MSI installer created successfully at bin\installer\$outputName" -ForegroundColor Green
Write-Host "Installer size: $((Get-Item "bin\installer\$outputName").Length / 1MB) MB" -ForegroundColor Cyan