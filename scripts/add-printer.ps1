<#
.SYNOPSIS
  Adds the ImageWriter II to Windows as an IPP printer (Microsoft IPP Class Driver), pointing at the service.

.DESCRIPTION
  Uses Add-Printer -IppURL, the directed-discovery path of the built-in IPP class driver. No vendor driver,
  no signing, no INF. Works on Windows 10 21H2+ / Windows 11.

  Windows reads the printer's capabilities once, when the queue is created, and caches them. So this script
  must be re-run after anything that changes what the service advertises - in particular after changing
  "Ribbon" in appsettings.json, because colour support is part of that capability set.

  After creating the queue the script sets the queue's default colour mode from what the service advertises
  (Set-PrintConfiguration -Color), so a colour ribbon prints in colour without touching Printing preferences.

.PARAMETER Name
  Printer queue name in Windows.

.PARAMETER Url
  IPP endpoint of the service. Defaults to the local machine, on whatever HttpPort the installed
  appsettings.json specifies.

.PARAMETER InstallDir
  Where the service is installed; only used to read HttpPort for the default -Url.

.PARAMETER Color
  Force the queue's default colour mode: Auto (default, follow the service), Yes or No.
#>
[CmdletBinding()]
param(
    [string]$Name = "ImageWriter II",
    [string]$Url,
    [ValidateSet("Auto", "Yes", "No")]
    [string]$Color = "Auto",
    [string]$InstallDir = "$env:ProgramFiles\ImageWriterII"
)

$ErrorActionPreference = "Stop"

# Default the endpoint from the installed configuration rather than assuming 631, so a changed HttpPort
# still works without the caller having to spell out -Url.
if (-not $Url) {
    $httpPort = 631
    $configPath = Join-Path $InstallDir "appsettings.json"
    if (Test-Path $configPath) {
        try {
            $json = (Get-Content $configPath -Raw) -replace '(?m)^\s*//.*$', ''   # the config allows // comments
            $port = ($json | ConvertFrom-Json).ImageWriter.HttpPort
            if ($null -ne $port) { $httpPort = [int]$port }
        } catch {
            Write-Warning "Could not read HttpPort from $configPath; assuming $httpPort."
        }
    }
    $Url = "http://localhost:$httpPort/ipp/print"
}

# The service must be reachable first.
$statusUrl = $Url -replace '/ipp/print$', '/api/status'
try {
    $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 5
    Write-Host "Service is up: $($status.printer) - $($status.state) ($($status.message))"
} catch {
    throw "Cannot reach the service at $Url. Start it first (Start-Service ImageWriterII, or run ImageWriterII.Service.exe)."
}

# colorRibbon comes from /api/status; it is what the service advertises as color-supported over IPP.
$wantColor = switch ($Color) {
    "Yes" { $true }
    "No" { $false }
    default { [bool]$status.colorRibbon }
}
if ($Color -eq "Auto") {
    if ($wantColor) {
        Write-Host "Service reports a four-colour ribbon; the queue will default to colour."
    } else {
        Write-Warning "Service reports a black ribbon, so the queue will be monochrome. If a colour ribbon is fitted, set `"Ribbon`": `"Color`" in appsettings.json, restart the service and re-run this script."
    }
}

if (Get-Printer -Name $Name -ErrorAction SilentlyContinue) {
    Write-Host "A printer named '$Name' already exists; removing it first."
    Remove-Printer -Name $Name
}

Write-Host "Adding printer '$Name' via IPP at $Url ..."
Add-Printer -Name $Name -IppURL $Url

# Windows picks the queue's DEVMODE defaults from the cached capabilities; pin colour explicitly so it does
# not depend on that. This works against the Microsoft IPP Class Driver, but is best-effort: a driver that
# refuses the PrintTicket change should not fail the whole install.
try {
    Set-PrintConfiguration -PrinterName $Name -Color $wantColor -ErrorAction Stop
    Write-Host "Queue default colour mode set to $(if ($wantColor) { 'colour' } else { 'monochrome' })."
} catch {
    Write-Warning "Could not set the queue's default colour mode: $($_.Exception.Message)"
    Write-Warning "Set it by hand in Printer properties > Preferences if colour does not come out."
}

$p = Get-Printer -Name $Name
$p | Format-List Name, DriverName, PortName, PrinterStatus
Get-PrintConfiguration -PrinterName $Name | Format-List PrinterName, Color, PaperSize

Write-Host ""
Write-Host "Printer added. Print a test page with:" -ForegroundColor Green
Write-Host "  rundll32 printui.dll,PrintUIEntry /k /n `"$Name`""
