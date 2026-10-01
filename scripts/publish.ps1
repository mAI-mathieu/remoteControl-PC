param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $projectRoot "artifacts\$Runtime"
dotnet publish (Join-Path $projectRoot 'TvRemote.Server\TvRemote.Server.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\install-user.ps1') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\firewall.ps1') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $outputPath
$archivePath = Join-Path $projectRoot "artifacts\TV-Remote-$Runtime.zip"
Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $archivePath -Force
Write-Host "Ready: $outputPath\TvRemote.Server.exe"
Write-Host "Distribution: $archivePath"
