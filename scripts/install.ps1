<#
.SYNOPSIS
  Compila sayRevit e lo installa nella cartella degli add-in di Revit dell'utente corrente.
.EXAMPLE
  .\scripts\install.ps1 -RevitVersion 2025
  .\scripts\install.ps1 -RevitVersion 2024 -Configuration Release
#>
param(
    [ValidateSet("2024", "2025", "2026")]
    [string]$RevitVersion = "2025",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\SayRevit.Addin\SayRevit.Addin.csproj"

Write-Host "Compilazione per Revit $RevitVersion ($Configuration)..." -ForegroundColor Cyan
dotnet build $project -c $Configuration -p:RevitVersion=$RevitVersion
if ($LASTEXITCODE -ne 0) { throw "Compilazione non riuscita." }

$artifacts = Join-Path $root "artifacts\$RevitVersion"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $addins "SayRevit"

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $artifacts "*") -Destination $target -Recurse -Force
Copy-Item -Path (Join-Path $artifacts "SayRevit.addin") -Destination $addins -Force

Write-Host "Installato in: $target" -ForegroundColor Green
Write-Host "Manifest:      $(Join-Path $addins 'SayRevit.addin')"
Write-Host "Riavvia Revit $RevitVersion: troverai la scheda 'sayRevit' nella barra multifunzione."
