# Builds GpuGuard (requires .NET 10 SDK).
#   .\build.ps1                 -> .\dist\GpuGuard.exe   (framework-dependent, needs .NET 10 desktop runtime)
#   .\build.ps1 -SelfContained  -> .\release\GpuGuard.exe + GpuGuard.zip (single file, runs on machines without .NET)
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if ($SelfContained) {
    dotnet publish .\app\GpuGuard.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\release
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    Get-ChildItem .\release\* -Exclude GpuGuard.exe | Remove-Item -Recurse -Force
    Compress-Archive -Path .\release\GpuGuard.exe -DestinationPath .\release\GpuGuard.zip -Force
    Write-Host "`nDone: $PSScriptRoot\release\GpuGuard.exe (+ GpuGuard.zip)"
}
else {
    dotnet publish .\app\GpuGuard.csproj -c Release -o .\dist
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    Write-Host "`nDone: $PSScriptRoot\dist\GpuGuard.exe"
}
