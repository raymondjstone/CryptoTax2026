# Create a self-signed certificate for MSIX signing, trust it, and update the publish profile.
# Run this script as Administrator in PowerShell.

$subject = "CN=passp"
$certStore = "Cert:\CurrentUser\My"
$pubxmlPath = "Properties\PublishProfiles\win-x64-msix.pubxml"

# 1. Create self-signed code-signing certificate (valid 3 years)
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $subject `
    -KeyUsage DigitalSignature `
    -FriendlyName "CryptoTax2026 MSIX Signing" `
    -CertStoreLocation $certStore `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
    -NotAfter (Get-Date).AddYears(3)

$thumbprint = $cert.Thumbprint
Write-Host "Certificate created. Thumbprint: $thumbprint" -ForegroundColor Green

# 2. Export and import into Trusted Root so Windows trusts the package
$certPath = "$env:TEMP\CryptoTax2026.cer"
Export-Certificate -Cert "$certStore\$thumbprint" -FilePath $certPath | Out-Null

# This requires elevation (Run as Administrator)
Import-Certificate -FilePath $certPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
Remove-Item $certPath
Write-Host "Certificate added to Trusted People store." -ForegroundColor Green

# 3. Update the publish profile with the thumbprint
$xml = [xml](Get-Content $pubxmlPath)
$ns = $xml.Project.NamespaceURI
$pg = $xml.Project.PropertyGroup

# Set signing enabled and thumbprint
$signingNode = $pg.SelectSingleNode("//*[local-name()='AppxPackageSigningEnabled']")
if ($signingNode) {
    $signingNode.InnerText = "true"
} else {
    $elem = $xml.CreateElement("AppxPackageSigningEnabled", $ns)
    $elem.InnerText = "true"
    $pg.AppendChild($elem) | Out-Null
}

$thumbNode = $pg.SelectSingleNode("//*[local-name()='PackageCertificateThumbprint']")
if ($thumbNode) {
    $thumbNode.InnerText = $thumbprint
} else {
    $elem = $xml.CreateElement("PackageCertificateThumbprint", $ns)
    $elem.InnerText = $thumbprint
    $pg.AppendChild($elem) | Out-Null
}

$xml.Save((Resolve-Path $pubxmlPath).Path)
Write-Host "Updated $pubxmlPath with thumbprint." -ForegroundColor Green

Write-Host "`nDone! You can now publish with the win-x64-msix profile." -ForegroundColor Cyan
