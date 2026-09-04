<#
.SYNOPSIS
  Compila sayRevit e lo installa nella cartella degli add-in di Revit dell'utente corrente.
.DESCRIPTION
  Rileva l'installazione di Revit in "C:\Program Files\Autodesk\Revit <versione>", legge il runtime .NET
  su cui gira quel Revit (RevitAPI.runtimeconfig.json) e compila caricatore e add-in per lo stesso framework.

  Revit carica direttamente solo il caricatore (SayRevit.Loader.dll); l'add-in vero e le sue librerie
  vengono letti dalla cartella a ogni clic sul pulsante. Quindi con Revit APERTO lo script aggiorna
  l'add-in e il codice nuovo gira dal clic successivo; il riavvio serve solo se cambia il caricatore
  (o la prima volta, per passare al caricatore).

  Se qualcosa non va, stampa la procedura di installazione manuale.
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

function Show-ManualHelp {
    param([string]$Version)
    Write-Host ""
    Write-Host "--- INSTALLAZIONE MANUALE (se questo script non funziona) ---" -ForegroundColor Yellow
    Write-Host "1) Compila:  dotnet build src\SayRevit.Addin\SayRevit.Addin.csproj -c Release -p:RevitVersion=$Version"
    Write-Host "             dotnet build src\SayRevit.Loader\SayRevit.Loader.csproj -c Release -p:RevitVersion=$Version"
    Write-Host "2) Apri la cartella del progetto: i file compilati sono in  artifacts\$Version\"
    Write-Host "3) Crea la cartella:  %APPDATA%\Autodesk\Revit\Addins\$Version\SayRevit"
    Write-Host "4) Copia TUTTI I FILE contenuti in  artifacts\$Version\  DENTRO la cartella SayRevit appena creata"
    Write-Host "5) Copia il singolo file  SayRevit.addin  anche in  %APPDATA%\Autodesk\Revit\Addins\$Version\  (fuori da SayRevit)"
    Write-Host "6) Riavvia Revit"
    Write-Host "-------------------------------------------------------------" -ForegroundColor Yellow
}

function Same-File {
    param([string]$A, [string]$B)
    if (-not (Test-Path $A) -or -not (Test-Path $B)) { return $false }
    return (Get-FileHash $A -Algorithm SHA256).Hash -eq (Get-FileHash $B -Algorithm SHA256).Hash
}

try {
    $root = Split-Path -Parent $PSScriptRoot
    $addinProject = Join-Path $root "src\SayRevit.Addin\SayRevit.Addin.csproj"
    $loaderProject = Join-Path $root "src\SayRevit.Loader\SayRevit.Loader.csproj"
    if (-not (Test-Path $addinProject)) { throw "Progetto non trovato: $addinProject. Esegui lo script dalla cartella del repository." }
    if (-not (Test-Path $loaderProject)) { throw "Progetto non trovato: $loaderProject." }

    $running = @(Get-Process -Name "Revit" -ErrorAction SilentlyContinue)
    $revitOpen = $running.Count -gt 0
    if ($revitOpen) {
        Write-Host "Revit e' in esecuzione (PID $($running.Id -join ', ')): aggiorno a caldo, senza toccare i file bloccati." -ForegroundColor Cyan
    }

    # --- 1. Installazione di Revit --------------------------------------------------------
    if (-not $RevitDir) { $RevitDir = "C:\Program Files\Autodesk\Revit $RevitVersion" }
    $apiDll = Join-Path $RevitDir "RevitAPI.dll"
    $useApiDir = Test-Path $apiDll
    if ($useApiDir) {
        Write-Host "Revit $RevitVersion trovato in: $RevitDir" -ForegroundColor Cyan
    } else {
        $installed = Get-ChildItem "C:\Program Files\Autodesk" -Directory -Filter "Revit 20*" -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
        Write-Host "ATTENZIONE: Revit $RevitVersion non trovato in: $RevitDir" -ForegroundColor Yellow
        if ($installed) {
            Write-Host "Versioni di Revit trovate sul PC: $($installed -join ', ')" -ForegroundColor Yellow
            Write-Host "Se una di queste corrisponde, interrompi (CTRL+C) e rilancia con -RevitVersion giusto." -ForegroundColor Yellow
        }
        Write-Host "Continuo usando le librerie API dai pacchetti NuGet per Revit $RevitVersion." -ForegroundColor Yellow
    }

    # --- 2. Runtime .NET su cui gira quel Revit -------------------------------------------
    # Predefiniti se non si riesce a leggere dal PC: 2024 -> net48, 2025/2026 -> net8, 2027 -> net10
    $framework = switch ($RevitVersion) {
        "2024" { "net48" }
        "2027" { "net10.0-windows" }
        default { "net8.0-windows" }
    }
    $runtimeConfig = Join-Path $RevitDir "RevitAPI.runtimeconfig.json"
    if (Test-Path $runtimeConfig) {
        try {
            $cfg = Get-Content $runtimeConfig -Raw | ConvertFrom-Json
            $fw = @($cfg.runtimeOptions.frameworks) + @($cfg.runtimeOptions.framework) | Where-Object { $_ } | Select-Object -First 1
            if ($fw -and $fw.version) {
                $major = [int]("$($fw.version)".Split('.')[0])
                $framework = "net$major.0-windows"
                Write-Host "Revit $RevitVersion gira su .NET $($fw.version): compilo per $framework" -ForegroundColor Cyan
            }
        } catch {
            Write-Host "Impossibile leggere $runtimeConfig ($($_.Exception.Message)): uso il framework predefinito $framework" -ForegroundColor Yellow
        }
    } else {
        Write-Host "Compilo per il framework predefinito di Revit ${RevitVersion}: $framework" -ForegroundColor Cyan
    }

    # --- 3. SDK .NET disponibile -----------------------------------------------------------
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "Comando 'dotnet' non trovato. Installa l'SDK .NET da https://dotnet.microsoft.com/download, chiudi e riapri PowerShell, riprova."
    }
    if ($framework -ne "net48") {
        $need = [int]($framework.Substring(3).Split('.')[0])
        $sdks = @()
        foreach ($line in (& dotnet --list-sdks)) { $sdks += [int]("$line".Split('.')[0]) }
        if (-not ($sdks | Where-Object { $_ -ge $need })) {
            throw "Serve l'SDK .NET $need o superiore per compilare per $framework. SDK trovati: $((& dotnet --list-sdks) -join '; '). Scaricalo da https://dotnet.microsoft.com/download"
        }
    }

    # --- 4. Compilazione ---------------------------------------------------------------------
    Write-Host "Compilazione per Revit $RevitVersion ($Configuration, $framework)..." -ForegroundColor Cyan
    foreach ($project in @($addinProject, $loaderProject)) {
        $buildArgs = @($project, "-c", $Configuration, "-p:RevitVersion=$RevitVersion", "-p:RevitFramework=$framework")
        if ($useApiDir) { $buildArgs += "-p:RevitApiDir=$RevitDir" }
        & dotnet build @buildArgs
        if ($LASTEXITCODE -ne 0) { throw "Compilazione non riuscita (dotnet build ha restituito $LASTEXITCODE). Leggi gli errori qui sopra." }
    }

    # --- 5. Copia dei file -------------------------------------------------------------------
    $artifacts = Join-Path $root "artifacts\$RevitVersion"
    foreach ($needed in @("SayRevit.Addin.dll", "SayRevit.Loader.dll", "SayRevit.addin")) {
        if (-not (Test-Path (Join-Path $artifacts $needed))) { throw "File compilato non trovato: $(Join-Path $artifacts $needed)" }
    }
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
    $target = Join-Path $addins "SayRevit"
    $manifest = Join-Path $addins "SayRevit.addin"

    if (-not $revitOpen) {
        # installazione pulita
        if (Test-Path $target) { Remove-Item -Recurse -Force $target }
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -Path (Join-Path $artifacts "*") -Destination $target -Recurse -Force
        Copy-Item -Path (Join-Path $artifacts "SayRevit.addin") -Destination $manifest -Force

        Write-Host ""
        Write-Host "Installato in: $target" -ForegroundColor Green
        Write-Host "Manifest:      $manifest" -ForegroundColor Green
        Write-Host "Riavvia Revit ${RevitVersion}: troverai la scheda 'sayRevit' nella barra multifunzione." -ForegroundColor Green
        exit 0
    }

    # aggiornamento a caldo: si copia file per file, saltando quelli bloccati da Revit
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $locked = @()
    $copied = 0
    foreach ($file in Get-ChildItem $artifacts -File) {
        $dest = Join-Path $target $file.Name
        if (Same-File $file.FullName $dest) { continue }
        try {
            Copy-Item -Path $file.FullName -Destination $dest -Force -ErrorAction Stop
            $copied++
        } catch {
            $locked += $file.Name
        }
    }
    try {
        if (-not (Same-File (Join-Path $artifacts "SayRevit.addin") $manifest)) {
            Copy-Item -Path (Join-Path $artifacts "SayRevit.addin") -Destination $manifest -Force -ErrorAction Stop
        }
    } catch {
        $locked += "SayRevit.addin"
    }

    Write-Host ""
    Write-Host "Aggiornato a caldo in: $target  ($copied file copiati)" -ForegroundColor Green
    $needRestart = $false
    if ($locked -contains "SayRevit.Addin.dll") {
        $needRestart = $true
        Write-Host "SayRevit.Addin.dll e' bloccato: questo Revit ha ancora caricato l'add-in direttamente (installazione precedente)." -ForegroundColor Yellow
    }
    if ($locked -contains "SayRevit.Loader.dll") {
        $needRestart = $true
        Write-Host "Il caricatore (SayRevit.Loader.dll) e' cambiato ma e' bloccato da Revit." -ForegroundColor Yellow
    }
    $others = $locked | Where-Object { $_ -ne "SayRevit.Addin.dll" -and $_ -ne "SayRevit.Loader.dll" }
    if ($others) {
        $needRestart = $true
        Write-Host "File bloccati non aggiornati: $($others -join ', ')" -ForegroundColor Yellow
    }
    if ($needRestart) {
        Write-Host "Riavvia Revit ${RevitVersion} una volta: da quel momento gli aggiornamenti non lo richiederanno piu'." -ForegroundColor Yellow
    } else {
        Write-Host "Nessun riavvio necessario: al prossimo clic sul pulsante 'Testo -> Tubazioni/Canali' gira il codice nuovo." -ForegroundColor Green
    }
}
catch {
    Write-Host ""
    Write-Host "ERRORE: $($_.Exception.Message)" -ForegroundColor Red
    Show-ManualHelp -Version $RevitVersion
    exit 1
}
