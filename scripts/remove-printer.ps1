<#
.SYNOPSIS
  Removes the Windows printer queue created by add-printer.ps1.
#>
[CmdletBinding()]
param([string]$Name = "ImageWriter II")

$ErrorActionPreference = "Stop"
$p = Get-Printer -Name $Name -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "No printer named '$Name'."; return }
Remove-Printer -Name $Name
Write-Host "Removed printer '$Name'."
