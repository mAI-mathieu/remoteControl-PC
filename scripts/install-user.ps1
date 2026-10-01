# Run from the extracted release folder. No administrator account is required.
param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\TvRemote'))
$ErrorActionPreference = 'Stop'
$sourceExe = Join-Path $PSScriptRoot 'TvRemote.Server.exe'
if (!(Test-Path -LiteralPath $sourceExe)) { throw 'Extract the full release ZIP before running this installer.' }
$destination = [System.IO.Path]::GetFullPath($InstallDirectory)
$sourceDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
if ($destination -eq $sourceDirectory -or $destination.StartsWith($sourceDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Choose an installation folder outside the extracted release folder.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $PSScriptRoot | Copy-Item -Destination $destination -Recurse -Force
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcut = $shortcutShell.CreateShortcut((Join-Path $shortcutDirectory 'TV Remote.lnk'))
$shortcut.TargetPath = Join-Path $destination 'TvRemote.Server.exe'
$shortcut.WorkingDirectory = $destination
$shortcut.Save()
Start-Process -FilePath (Join-Path $destination 'TvRemote.Server.exe') -WindowStyle Hidden
Write-Host "Installed in $destination. Enable Start with Windows in the tray app if desired."
