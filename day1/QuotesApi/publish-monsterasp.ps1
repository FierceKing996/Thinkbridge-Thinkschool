# Builds the Angular UI, stages it into wwwroot/, and publishes the API to
# ./publish as a framework-dependent (.NET 10) deployment ready to upload to
# MonsterASP.NET. Run from anywhere; paths are resolved against this script.

$ErrorActionPreference = 'Stop'
$api = $PSScriptRoot
$repoRoot = Resolve-Path (Join-Path $api '..\..')
$ui = Join-Path $repoRoot 'quotes-ui'
$wwwroot = Join-Path $api 'wwwroot'
$publishDir = Join-Path $api 'publish'

Write-Host '==> Building Angular UI (production)' -ForegroundColor Cyan
Push-Location $ui
try {
    if (-not (Test-Path (Join-Path $ui 'node_modules'))) { npm ci }
    npm run build
} finally { Pop-Location }

Write-Host '==> Staging UI into wwwroot/' -ForegroundColor Cyan
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item -ItemType Directory -Path $wwwroot | Out-Null
Copy-Item (Join-Path $ui 'dist\quotes-ui\browser\*') $wwwroot -Recurse

Write-Host '==> dotnet publish (Release)' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $api 'QuotesApi.csproj') -c Release -o $publishDir

Write-Host ''
Write-Host "Publish output: $publishDir" -ForegroundColor Green
Write-Host 'Upload the CONTENTS of that folder to your MonsterASP site root (wwwroot on the host).'
