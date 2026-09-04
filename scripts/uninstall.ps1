param(
    [ValidateSet("2024", "2025", "2026", "2027")]
    [string]$RevitVersion = "2025"
)
# Con Revit aperto i file dell'add-in sono bloccati: la rimozione resterebbe a meta'.
$running = @(Get-Process -Name "Revit" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "Revit e' in esecuzione (PID $($running.Id -join ', ')): chiudilo e rilancia questo script." -ForegroundColor Red
    exit 1
}

$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
Remove-Item -Recurse -Force (Join-Path $addins "SayRevit") -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $addins "SayRevit.addin") -ErrorAction SilentlyContinue
Write-Host "sayRevit rimosso da Revit $RevitVersion."
