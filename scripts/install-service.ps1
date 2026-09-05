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

.PARAMETER NoBuild
  Skip dotnet publish and install whatever is already in .\publish.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\ImageWriterII",
    [string]$Port = "COM1",
    [ValidateSet("Auto", "RequestToSend", "DataSetReady", "DataCarrierDetect", "XOnXOff", "RequestToSendXOnXOff", "None")]
    [string]$Handshake = "Auto",
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

# set the serial port in the config (only when the config was freshly copied or the caller passed -Port explicitly)
$configPath = Join-Path $InstallDir "appsettings.json"
if ($PSBoundParameters.ContainsKey("Port") -or $PSBoundParameters.ContainsKey("Handshake") -or -not (Test-Path (Join-Path $InstallDir "printer-uuid.txt"))) {
    $text = Get-Content $configPath -Raw
    $text = [regex]::Replace($text, '"PortName":\s*"[^"]*"', "`"PortName`": `"$Port`"")
    $text = [regex]::Replace($text, '"Handshake":\s*"[^"]*"', "`"Handshake`": `"$Handshake`"")
    Set-Content -Path $configPath -Value $text -Encoding UTF8
    Write-Host "Serial port set to $Port, handshake $Handshake in $configPath"
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

Write-Host "Configuring firewall rules" -ForegroundColor Cyan
foreach ($rule in @(
    @{ Name = "ImageWriterII IPP (TCP 631)"; Protocol = "TCP"; Port = 631 },
    @{ Name = "ImageWriterII mDNS (UDP 5353)"; Protocol = "UDP"; Port = 5353 },
    @{ Name = "ImageWriterII raw print (TCP 9100)"; Protocol = "TCP"; Port = 9100 })) {
    if (-not (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Protocol $rule.Protocol -LocalPort $rule.Port -Program $exe -Profile Private,Domain | Out-Null
    }
}

Write-Host "Starting service" -ForegroundColor Cyan
Start-Service -Name $serviceName
Start-Sleep -Seconds 3
Get-Service -Name $serviceName | Format-Table -AutoSize

Write-Host ""
Write-Host "Done. Status page: http://localhost:631/" -ForegroundColor Green
Write-Host "Add the printer with:  .\scripts\add-printer.ps1   (or Settings > Printers & scanners > Add device)"
Write-Host "Logs: $InstallDir\logs"
