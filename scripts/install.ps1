<#
.SYNOPSIS
  Compila sayRevit e lo installa nella cartella degli add-in di Revit dell'utente corrente.
.DESCRIPTION
  Rileva l'installazione di Revit in "C:\Program Files\Autodesk\Revit <versione>", legge il runtime .NET
  su cui gira quel Revit (RevitAPI.runtimeconfig.json) e compila l'add-in per lo stesso framework,
  usando le librerie API della cartella di Revit. Così l'add-in combacia sempre con il Revit installato.
.EXAMPLE
  .\scripts\install.ps1 -RevitVersion 2025
  .\scripts\install.ps1 -RevitVersion 2027 -Configuration Release
#>
param(
    [ValidateSet("2024", "2025", "2026", "2027")]
    [string]$RevitVersion = "2025",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$RevitDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\SayRevit.Addin\SayRevit.Addin.csproj"

# --- 1. Installazione di Revit -------------------------------------------------------------
if (-not $RevitDir) { $RevitDir = "C:\Program Files\Autodesk\Revit $RevitVersion" }
$installed = Get-ChildItem "C:\Program Files\Autodesk" -Directory -Filter "Revit 20*" -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
if (-not (Test-Path (Join-Path $RevitDir "RevitAPI.dll"))) {
    Write-Host "Revit $RevitVersion non trovato in: $RevitDir" -ForegroundColor Red
    if ($installed) { Write-Host "Versioni di Revit installate: $($installed -join ', ')" }
    throw "Indica la versione di Revit effettivamente installata (-RevitVersion) oppure la cartella (-RevitDir)."
}
Write-Host "Revit $RevitVersion trovato in: $RevitDir" -ForegroundColor Cyan

# --- 2. Runtime .NET su cui gira quel Revit -------------------------------------------------
$framework = "net48"
$runtimeConfig = Join-Path $RevitDir "RevitAPI.runtimeconfig.json"
if (Test-Path $runtimeConfig) {
    $cfg = Get-Content $runtimeConfig -Raw | ConvertFrom-Json
    $fw = @($cfg.runtimeOptions.frameworks) + @($cfg.runtimeOptions.framework) | Where-Object { $_ } | Select-Object -First 1
    if (-not $fw) { throw "Formato inatteso di $runtimeConfig" }
    $major = [int]($fw.version.Split('.')[0])
    $framework = "net$major.0-windows"
    Write-Host "Revit $RevitVersion gira su .NET $($fw.version): compilo per $framework" -ForegroundColor Cyan
} elseif ($RevitVersion -ne "2024") {
    throw "File $runtimeConfig non trovato: impossibile determinare il runtime .NET di Revit."
} else {
    Write-Host "Revit 2024 gira su .NET Framework 4.8: compilo per net48" -ForegroundColor Cyan
}

# --- 3. SDK .NET disponibile ----------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Comando 'dotnet' non trovato: installa l'SDK .NET da https://dotnet.microsoft.com/download e riapri PowerShell."
}
if ($framework -ne "net48") {
    $need = [int]($framework.Substring(3).Split('.')[0])
    $sdks = (dotnet --list-sdks) | ForEach-Object { [int]($_.Split('.')[0]) }
    if (-not ($sdks | Where-Object { $_ -ge $need })) {
        throw "Serve l'SDK .NET $need (o superiore) per compilare per $framework. SDK installati: $((dotnet --list-sdks) -join '; ')"
    }
}

# --- 4. Compilazione --------------------------------------------------------------------------
Write-Host "Compilazione per Revit $RevitVersion ($Configuration, $framework)..." -ForegroundColor Cyan
dotnet build $project -c $Configuration -p:RevitVersion=$RevitVersion -p:RevitFramework=$framework "-p:RevitApiDir=$RevitDir"
if ($LASTEXITCODE -ne 0) { throw "Compilazione non riuscita." }

# --- 5. Installazione -------------------------------------------------------------------------
$artifacts = Join-Path $root "artifacts\$RevitVersion"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $addins "SayRevit"

if (Test-Path $target) { Remove-Item -Recurse -Force $target }
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $artifacts "*") -Destination $target -Recurse -Force
Copy-Item -Path (Join-Path $artifacts "SayRevit.addin") -Destination $addins -Force

Write-Host "Installato in: $target" -ForegroundColor Green
Write-Host "Manifest:      $(Join-Path $addins 'SayRevit.addin')"
Write-Host "Riavvia Revit $RevitVersion: troverai la scheda 'sayRevit' nella barra multifunzione."
