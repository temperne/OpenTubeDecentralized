# OpenTube's .NET node for Windows, from one command (the site's Devices page makes it for you), in Command Prompt or
# PowerShell:
#   powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm https://<site>/opentube-dotnet.ps1))) --site=… --grant=…"
# Installs .NET 10 if this computer doesn't have it (for this user, no admin rights needed), downloads OpenTube (no git
# needed) and starts the node in a window. Running it again updates.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without its progress bar
$repo = if ($env:OPENTUBE_REPO) { $env:OPENTUBE_REPO } else { 'temperne/OpenTubeDecentralized' }
$ref = if ($env:OPENTUBE_REF) { $env:OPENTUBE_REF } else { 'ccr-500677da-r1uf50' }
$app = Join-Path $HOME '.opentube\dotnet-app'
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) { $env:DOTNET_ROOT = $userDotnet; $env:PATH = "$userDotnet;$env:PATH" }

function Test-Dotnet10 { try { return [bool]((& dotnet --list-sdks 2>$null) -match '^(1[0-9]|[2-9][0-9])\.') } catch { return $false } }
if (-not (Test-Dotnet10)) {
  Write-Host 'OpenTube: installing .NET 10 for this user (once; no admin rights needed)...'
  $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
  Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
  & $installer -Channel 10.0 -InstallDir $userDotnet
  $env:DOTNET_ROOT = $userDotnet; $env:PATH = "$userDotnet;$env:PATH"
  if (-not (Test-Dotnet10)) { Write-Host 'OpenTube: .NET 10 did not install. Install it from https://dot.net and run this again.'; exit 1 }
}

Write-Host 'OpenTube: downloading the latest version...'
$zip = Join-Path $env:TEMP 'opentube.zip'; $tmp = Join-Path $env:TEMP 'opentube-src'
$commit = ''
try { $commit = (Invoke-WebRequest -UseBasicParsing -Headers @{ Accept = 'application/vnd.github.sha' } "https://api.github.com/repos/$repo/commits/$ref").Content.Trim() } catch { }
if ($commit -notmatch '^[0-9a-f]{40}$') { $commit = '' }
$what = if ($commit) { $commit } else { "refs/heads/$ref" }
Invoke-WebRequest -UseBasicParsing "https://codeload.github.com/$repo/zip/$what" -OutFile $zip
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Expand-Archive -Path $zip -DestinationPath $tmp
New-Item -ItemType Directory -Force -Path $app | Out-Null
Copy-Item -Recurse -Force (Join-Path (Get-ChildItem $tmp | Select-Object -First 1).FullName '*') $app
Set-Content -NoNewline -Path (Join-Path $app '.commit') -Value $commit
# the node updates itself: it fetches the new source here and exits with 75; this loop starts it again (rebuilding)
$env:OPENTUBE_APP_DIR = $app; $env:OPENTUBE_REPO = $repo; $env:OPENTUBE_REF = $ref
Set-Location (Join-Path $app 'dotnet')
Write-Host 'OpenTube: building and starting (the first time takes a minute)...'
while ($true) {
  & dotnet run -c Release --project OpenTube.App -- @args
  if ($LASTEXITCODE -ne 75) { exit $LASTEXITCODE }
  Write-Host 'OpenTube: starting the new version...'
}
