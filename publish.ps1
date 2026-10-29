# NekoDex Release: eine self-contained Single-File-EXE, ohne Begleitdateien im Ordner.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$publish = Join-Path $root "bin\Release\net10.0-windows\win-x64\publish"
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

dotnet publish -c Release -r win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen ($LASTEXITCODE)." }

$exe = Join-Path $publish "NekoDex.exe"
if (-not (Test-Path $exe)) { throw "NekoDex.exe fehlt." }

$extra = @(Get-ChildItem $publish -Force | Where-Object { $_.Name -ne "NekoDex.exe" })
if ($extra.Count -gt 0) {
    $names = ($extra | ForEach-Object { $_.Name }) -join ", "
    throw "Publish-Ordner enthält Zusatzdateien: $names"
}

Write-Host "Release: $exe ($((Get-Item $exe).Length) Bytes)"
