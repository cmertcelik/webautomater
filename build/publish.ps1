# Kullanım: ./build/publish.ps1 [-Rids win-x64,linux-x64]
param([string[]]$Rids = @("win-x64", "linux-x64", "osx-arm64", "osx-x64"))
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
foreach ($rid in $Rids) {
    Write-Host "==> $rid"
    foreach ($proj in @("src/WebAutomater.Cli", "src/WebAutomater.Gui")) {
        dotnet publish $proj -c Release -r $rid --self-contained true -o "dist/$rid" --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
Write-Host "Bitti: dist/"
