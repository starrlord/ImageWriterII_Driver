<#
.SYNOPSIS
  Adds the ImageWriter II to Windows as an IPP printer (Microsoft IPP Class Driver), pointing at the service.

.DESCRIPTION
  Uses Add-Printer -IppURL, the directed-discovery path of the built-in IPP class driver. No vendor driver,
  no signing, no INF. Works on Windows 10 21H2+ / Windows 11.

.PARAMETER Name
  Printer queue name in Windows.

.PARAMETER Url
  IPP endpoint of the service. Defaults to the local machine.
#>
[CmdletBinding()]
param(
    [string]$Name = "ImageWriter II",
    [string]$Url = "http://localhost:631/ipp/print"
)

$ErrorActionPreference = "Stop"

# The service must be reachable first.
try {
    $status = Invoke-RestMethod -Uri ($Url -replace '/ipp/print$', '/api/status') -TimeoutSec 5
    Write-Host "Service is up: $($status.printer) - $($status.state) ($($status.message))"
} catch {
    throw "Cannot reach the service at $Url. Start it first (Start-Service ImageWriterII, or run ImageWriterII.Service.exe)."
}

if (Get-Printer -Name $Name -ErrorAction SilentlyContinue) {
    Write-Host "A printer named '$Name' already exists; removing it first."
    Remove-Printer -Name $Name
}

Write-Host "Adding printer '$Name' via IPP at $Url ..."
Add-Printer -Name $Name -IppURL $Url

$p = Get-Printer -Name $Name
$p | Format-List Name, DriverName, PortName, PrinterStatus
Write-Host ""
Write-Host "Printer added. Print a test page with:" -ForegroundColor Green
Write-Host "  rundll32 printui.dll,PrintUIEntry /k /n `"$Name`""
