param(
    [ValidateSet("2024", "2025", "2026", "2027")]
    [string]$RevitVersion = "2025"
)
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
Remove-Item -Recurse -Force (Join-Path $addins "SayRevit") -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $addins "SayRevit.addin") -ErrorAction SilentlyContinue
Write-Host "sayRevit rimosso da Revit $RevitVersion."
