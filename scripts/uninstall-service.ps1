<#
.SYNOPSIS
  Removes the ImageWriterII Windows service, its firewall rules and (optionally) the install directory.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\ImageWriterII",
    [switch]$RemoveFiles
)

$ErrorActionPreference = "Stop"
$serviceName = "ImageWriterII"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell."
}

$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Stopped") { Stop-Service -Name $serviceName -Force }
    sc.exe delete $serviceName | Out-Null
    Write-Host "Service removed."
} else {
    Write-Host "Service not installed."
}

foreach ($name in "ImageWriterII IPP (TCP 631)", "ImageWriterII mDNS (UDP 5353)", "ImageWriterII raw print (TCP 9100)") {
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Removed $InstallDir"
}
