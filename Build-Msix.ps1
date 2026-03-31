<#
.SYNOPSIS
    Builds a signed MSIX package for CryptoTax2026.

.DESCRIPTION
    This script:
    1. Creates a self-signed certificate (if one doesn't already exist)
    2. Builds the project in Release mode
    3. Produces a signed .msix package ready for sideloading

.PARAMETER Platform
    Target platform: x64 (default), x86, or ARM64.

.PARAMETER SkipCert
    Skip certificate creation (use existing certificate).

.PARAMETER CertThumbprint
    Use an existing certificate by thumbprint instead of creating a new one.

.EXAMPLE
    .\Build-Msix.ps1
    .\Build-Msix.ps1 -Platform x86
    .\Build-Msix.ps1 -CertThumbprint "ABC123..."
#>

param(
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",

    [switch]$SkipCert,

    [string]$CertThumbprint
)

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir "CryptoTax2026.csproj"
$CertDir = Join-Path $ProjectDir "Certificates"
$CertPath = Join-Path $CertDir "CryptoTax2026_DevCert.pfx"
$CertPassword = "CryptoTax2026Dev"
$Publisher = "CN=passp"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  CryptoTax2026 MSIX Package Builder"    -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Certificate
if ($CertThumbprint) {
    Write-Host "[1/3] Using existing certificate: $CertThumbprint" -ForegroundColor Yellow
    $thumbprint = $CertThumbprint
}
elseif (-not $SkipCert) {
    Write-Host "[1/3] Setting up signing certificate..." -ForegroundColor Yellow

    # Check for existing cert in store
    $existingCert = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($existingCert) {
        Write-Host "  Found existing valid certificate: $($existingCert.Thumbprint)" -ForegroundColor Green
        $thumbprint = $existingCert.Thumbprint
    }
    else {
        Write-Host "  Creating new self-signed certificate..." -ForegroundColor White

        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $Publisher `
            -KeyUsage DigitalSignature `
            -FriendlyName "CryptoTax2026 Development Certificate" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
            -NotAfter (Get-Date).AddYears(3)

        $thumbprint = $cert.Thumbprint
        Write-Host "  Certificate created: $thumbprint" -ForegroundColor Green

        # Export .pfx for backup
        if (-not (Test-Path $CertDir)) {
            New-Item -ItemType Directory -Path $CertDir | Out-Null
        }
        $securePassword = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
        Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$thumbprint" -FilePath $CertPath -Password $securePassword | Out-Null
        Write-Host "  Certificate exported to: $CertPath" -ForegroundColor Green

        # Trust the certificate (install to Trusted People)
        Write-Host "  Installing certificate to Trusted People store..." -ForegroundColor White
        $certObj = Get-Item "Cert:\CurrentUser\My\$thumbprint"
        $trustedStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "CurrentUser")
        $trustedStore.Open("ReadWrite")
        $trustedStore.Add($certObj)
        $trustedStore.Close()
        Write-Host "  Certificate trusted for sideloading." -ForegroundColor Green
    }
}
else {
    Write-Host "[1/3] Skipping certificate setup." -ForegroundColor Yellow
    $thumbprint = ""
}

Write-Host ""

# Step 2: Bump version
Write-Host "[2/4] Bumping version number..." -ForegroundColor Yellow

$manifestPath = Join-Path $ProjectDir "Package.appxmanifest"
$manifestXml = [xml](Get-Content $manifestPath -Raw)
$ns = New-Object Xml.XmlNamespaceManager($manifestXml.NameTable)
$ns.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$identityNode = $manifestXml.SelectSingleNode("//m:Identity", $ns)
$oldVersion = [version]$identityNode.Version

# Increment the build (third) component: Major.Minor.Build.Revision
$newVersion = [version]::new($oldVersion.Major, $oldVersion.Minor, $oldVersion.Build + 1, 0)
$newVersionStr = $newVersion.ToString()

# Update Package.appxmanifest
$identityNode.Version = $newVersionStr
$manifestXml.Save($manifestPath)

# Update AssemblyVersion / FileVersion in .csproj
$csprojContent = Get-Content $ProjectFile -Raw
$csprojContent = $csprojContent -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$newVersionStr</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$newVersionStr</FileVersion>"
Set-Content -Path $ProjectFile -Value $csprojContent -NoNewline

Write-Host "  Version: $oldVersion -> $newVersionStr" -ForegroundColor Green
Write-Host ""

# Step 3: Build
Write-Host "[3/4] Building MSIX package ($Platform | Release)..." -ForegroundColor Yellow
Write-Host ""

$buildArgs = @(
    "publish"
    $ProjectFile
    "-c", "Release"
    "-p:Platform=$Platform"
    "-p:RuntimeIdentifier=win-$($Platform.ToLower())"
    "-p:GenerateAppxPackageOnBuild=true"
    "-p:AppxBundle=Never"
    "-p:AppxPackageSigningEnabled=true"
    "-p:AppxPackageDir=$ProjectDir\bin\publish\msix\packages\"
    "--self-contained", "true"
)

if ($thumbprint) {
    $buildArgs += "-p:PackageCertificateThumbprint=$thumbprint"
}

& dotnet $buildArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Build FAILED." -ForegroundColor Red
    exit 1
}

Write-Host ""

# Step 4: Output
Write-Host "[4/4] Locating output..." -ForegroundColor Yellow

$msixDir = Join-Path $ProjectDir "bin\publish\msix\packages"
$msixFiles = Get-ChildItem -Path $msixDir -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue

if ($msixFiles) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  BUILD SUCCEEDED" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    foreach ($f in $msixFiles) {
        $sizeMB = [math]::Round($f.Length / 1MB, 2)
        Write-Host "  Package: $($f.FullName)" -ForegroundColor White
        Write-Host "  Size:    $sizeMB MB" -ForegroundColor White
    }
    Write-Host ""
    Write-Host "  To install, double-click the .msix file or run:" -ForegroundColor Cyan
    Write-Host "    Add-AppxPackage -Path `"$($msixFiles[0].FullName)`"" -ForegroundColor Cyan
    Write-Host ""
}
else {
    # Check for .msixupload or .appx
    $anyPackage = Get-ChildItem -Path $msixDir -Recurse -Include "*.msix","*.msixupload","*.appx" -ErrorAction SilentlyContinue
    if ($anyPackage) {
        Write-Host "  Packages found:" -ForegroundColor White
        $anyPackage | ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor White }
    }
    else {
        Write-Host "  Warning: No .msix file found in output directory." -ForegroundColor Yellow
        Write-Host "  Check: $msixDir" -ForegroundColor Yellow
    }
}
