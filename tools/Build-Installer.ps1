param([string]$IsccPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path -LiteralPath $IsccPath)) { throw 'Install Inno Setup 6 or pass -IsccPath with the path to ISCC.exe.' }
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'HaNotify\HaNotify.csproj')
$version = $project.Project.PropertyGroup.Version
$releaseDir = Join-Path $repoRoot "artifacts\$version"
$publishDir = Join-Path $releaseDir ('app-' + [Guid]::NewGuid().ToString('N'))
& dotnet publish (Join-Path $repoRoot 'HaNotify\HaNotify.csproj') -c Release -o $publishDir --self-contained true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
& $IsccPath "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DReleaseDir=$releaseDir" (Join-Path $repoRoot 'installer\HaNotify.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$installer = Join-Path $releaseDir "HA-Notify-$version-Setup-x64.exe"
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath (Join-Path $releaseDir 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Installer: $installer"
