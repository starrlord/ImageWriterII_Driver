<#
.SYNOPSIS
  Builds and installs the ImageWriter II IPP Everywhere service as a Windows service.

.DESCRIPTION
  - Publishes the service to the install directory (default C:\Program Files\ImageWriterII)
  - Registers the "ImageWriterII" Windows service (automatic start, restart on failure)
  - Opens the firewall for IPP (TCP 631), mDNS (UDP 5353) and the raw port (TCP 9100)
  - Starts the service
  Run from an elevated PowerShell prompt.

.PARAMETER InstallDir
  Where to put the binaries and configuration.

.PARAMETER Port
  Serial port the ImageWriter II is connected to (written into appsettings.json).

.PARAMETER Handshake
  Flow-control mode written into appsettings.json. Auto (default) picks whichever of CTS/DSR/DCD carries the printer's ready signal.

.PARAMETER Ribbon
  Which ribbon is fitted, written into appsettings.json. Color (default) advertises the four-colour ribbon to
  clients so they print in colour; Black forces monochrome; Auto asks the printer with ESC ? at startup, which
  only works on cables that carry the printer-to-PC data line and otherwise falls back to Black.

.PARAMETER FirewallProfile
  Which Windows firewall profiles the inbound rules apply to. Any (default) because Windows classifies most
  home networks as Public and AirPrint clients live on the LAN; the rules are still scoped to this program
  and to ports 631 / 5353 / 9100.

.PARAMETER NoBuild
  Skip dotnet publish and install whatever is already in .\publish.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\ImageWriterII",
    [string]$Port = "COM1",
    [ValidateSet("Auto", "RequestToSend", "DataSetReady", "DataCarrierDetect", "XOnXOff", "RequestToSendXOnXOff", "None")]
    [string]$Handshake = "Auto",
    [ValidateSet("Color", "Black", "Auto")]
    [string]$Ribbon = "Color",
    [ValidateSet("Any", "Domain", "Private", "Public", "NotApplicable")]
    [string[]]$FirewallProfile = @("Any"),
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$serviceName = "ImageWriterII"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell."
}

if (-not $NoBuild) {
    Write-Host "Publishing service and tools..." -ForegroundColor Cyan
    dotnet publish (Join-Path $repoRoot "src\ImageWriterII.Service\ImageWriterII.Service.csproj") -c Release -r win-x64 --self-contained false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the service" }
    dotnet publish (Join-Path $repoRoot "src\iwprint\iwprint.csproj") -c Release -r win-x64 --self-contained false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for iwprint" }
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..." -ForegroundColor Cyan
    if ($existing.Status -ne "Stopped") { Stop-Service -Name $serviceName -Force }
    Start-Sleep -Seconds 1
}

Write-Host "Installing to $InstallDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# keep an existing configuration and uuid
$keep = @("appsettings.json", "printer-uuid.txt")
Get-ChildItem $publishDir | ForEach-Object {
    if ($keep -contains $_.Name -and (Test-Path (Join-Path $InstallDir $_.Name))) { return }
    Copy-Item $_.FullName -Destination $InstallDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir "logs"), (Join-Path $InstallDir "spool") | Out-Null

# Write the serial port, handshake and ribbon into the config. Only on a fresh install (no printer-uuid.txt
# yet) or when the caller passed the value explicitly, so re-running the script never clobbers a tuned config.
$configPath = Join-Path $InstallDir "appsettings.json"
$freshInstall = -not (Test-Path (Join-Path $InstallDir "printer-uuid.txt"))
if ($freshInstall -or $PSBoundParameters.ContainsKey("Port") -or $PSBoundParameters.ContainsKey("Handshake") -or $PSBoundParameters.ContainsKey("Ribbon")) {
    $text = Get-Content $configPath -Raw
    if ($freshInstall -or $PSBoundParameters.ContainsKey("Port")) {
        $text = [regex]::Replace($text, '"PortName":\s*"[^"]*"', "`"PortName`": `"$Port`"")
    }
    if ($freshInstall -or $PSBoundParameters.ContainsKey("Handshake")) {
        $text = [regex]::Replace($text, '"Handshake":\s*"[^"]*"', "`"Handshake`": `"$Handshake`"")
    }
    if ($freshInstall -or $PSBoundParameters.ContainsKey("Ribbon")) {
        $text = [regex]::Replace($text, '"Ribbon":\s*"[^"]*"', "`"Ribbon`": `"$Ribbon`"")
    }
    Set-Content -Path $configPath -Value $text -Encoding UTF8
    Write-Host "Config: port $Port, handshake $Handshake, ribbon $Ribbon ($configPath)"
} else {
    Write-Host "Keeping the existing $configPath (pass -Port / -Handshake / -Ribbon to change it)."
}

$exe = Join-Path $InstallDir "ImageWriterII.Service.exe"
if (-not $existing) {
    Write-Host "Registering service $serviceName" -ForegroundColor Cyan
    New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName "ImageWriter II IPP Print Service" `
        -Description "IPP Everywhere / AirPrint print service for the Apple ImageWriter II on $Port" -StartupType Automatic | Out-Null
} else {
    sc.exe config $serviceName binPath= "`"$exe`"" | Out-Null
}
# restart on failure
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# Firewall. Windows classifies most home Ethernet/Wi-Fi networks as Public, and AirPrint from a phone comes
# from the LAN, so the default covers every profile. The rules stay narrow: this program, these three ports,
# inbound only. Pass -FirewallProfile Private,Domain to restrict them.
Write-Host "Configuring firewall rules ($FirewallProfile)" -ForegroundColor Cyan
foreach ($rule in @(
    @{ Name = "ImageWriterII IPP (TCP 631)"; Protocol = "TCP"; Port = 631 },
    @{ Name = "ImageWriterII mDNS (UDP 5353)"; Protocol = "UDP"; Port = 5353 },
    @{ Name = "ImageWriterII raw print (TCP 9100)"; Protocol = "TCP"; Port = 9100 })) {
    $existingRule = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
    if ($existingRule) {
        # keep it in step with -FirewallProfile and with a moved install directory
        $existingRule | Set-NetFirewallRule -Program $exe -Profile $FirewallProfile -Enabled True | Out-Null
    } else {
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Protocol $rule.Protocol `
            -LocalPort $rule.Port -Program $exe -Profile $FirewallProfile | Out-Null
    }
}
$publicLan = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.NetworkCategory -eq "Public" }
if ($publicLan -and $FirewallProfile -notcontains "Any" -and $FirewallProfile -notcontains "Public") {
    Write-Warning "$($publicLan.InterfaceAlias -join ', ') is a Public network but the firewall rules exclude Public; iPhones/iPads on that network will not see or reach the printer."
}

Write-Host "Starting service" -ForegroundColor Cyan
Start-Service -Name $serviceName
Start-Sleep -Seconds 3
Get-Service -Name $serviceName | Format-Table -AutoSize

try {
    $status = Invoke-RestMethod -Uri "http://localhost:631/api/status" -TimeoutSec 5
    Write-Host ("Service reports: {0}, {1} ribbon, port {2}" -f $status.state, $(if ($status.colorRibbon) { "four-colour" } else { "black" }), $status.port)
} catch {
    Write-Warning "The service is registered but did not answer on http://localhost:631/ yet. Check $InstallDir\logs."
}

Write-Host ""
Write-Host "Done. Status page: http://localhost:631/" -ForegroundColor Green
Write-Host "Add the printer with:  .\scripts\add-printer.ps1   (or Settings > Printers & scanners > Add device)"
Write-Host "iPhone / iPad / Mac need no setup: the printer is announced over Bonjour as AirPrint."
Write-Host "Logs: $InstallDir\logs"
