# Builds the zip to upload in ODC Portal (External libraries > Create library from external code).
# The zip holds the publish output at its root, with no parent folder.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$publish = Join-Path $PSScriptRoot 'out/publish'
$zip = Join-Path $PSScriptRoot 'out/McpTriage.zip'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish McpTriage/McpTriage.csproj -c Release -r linux-x64 --no-self-contained -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Write-Host "Library zip: $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"
