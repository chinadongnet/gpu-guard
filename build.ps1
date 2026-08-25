# Builds GpuGuard and publishes it to .\dist\GpuGuard.exe (requires .NET 10 SDK).
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet publish .\app\GpuGuard.csproj -c Release -o .\dist
Write-Host "`nDone: $PSScriptRoot\dist\GpuGuard.exe"
