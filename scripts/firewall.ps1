# This optional action changes Windows Firewall and requires an elevated PowerShell.
#requires -RunAsAdministrator
param([string]$Executable = (Join-Path $PSScriptRoot 'TvRemote.Server.exe'), [ValidateRange(1024,65535)][int]$HttpPort = 8123, [ValidateRange(1024,65535)][int]$HttpsPort = 8124)
$ErrorActionPreference = 'Stop'
$resolvedExe = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetFileName($resolvedExe) -ne 'TvRemote.Server.exe') { throw 'Choose the published TV Remote executable.' }
New-NetFirewallRule -DisplayName 'TV Remote - private LAN web' -Direction Inbound -Program $resolvedExe -Protocol TCP -LocalPort $HttpPort,$HttpsPort -RemoteAddress LocalSubnet -Profile Private -Action Allow | Out-Null
New-NetFirewallRule -DisplayName 'TV Remote - private LAN discovery' -Direction Inbound -Program $resolvedExe -Protocol UDP -LocalPort 5353 -RemoteAddress LocalSubnet -Profile Private -Action Allow | Out-Null
Write-Host 'Private-network, local-subnet rules created. No router settings were changed.'
