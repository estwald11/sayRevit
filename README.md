# sayRevit

Autodesk Revit add-in that creates MEP piping from structured input, using the pipe types,
system types and levels already loaded in the project. Two modes:

- **Text mode** — describe pipes and ducts in Italian or English
  (*"una tubazione DN200 lunga 10 m con 3 stacchi DN15"*). A deterministic rule-based
  parser runs offline; optionally, Claude (official Anthropic SDK, structured output)
  interprets free-form sentences.
- **Manifold mode (Collettore)** — fully parametric supply/return manifold builder.
  No natural language: every value comes from explicit fields.

Both modes show a preview with notes and warnings before anything is created. Creation
runs in a single transaction (one Undo step), and created elements are selected at the end.

## Manifold mode

- Circuits are entered one per row by DN; the pipe material/type is picked from the
  project's loaded types (exact-name match).
- Header DN is computed from **D = √(1.5·(S₁+S₂+…)/0.785)** on the circuit cross-sections,
  then resolved to the type's size with the smallest inner diameter ≥ D. Manual override
  available.
- Two identical headers (supply/return), parallel and aligned, built along +X; the second
  header's stubs are interleaved at half the circuit spacing. Headers extend 5 cm past the
  edge of the governing end stubs.
- Circuit stubs are overlapped on the header axis (no tee fittings, headers stay uncut).
- **Enddeckel** end caps are placed, sized and connected automatically on all four header
  ends (stainless and carbon steel families).

## Requirements

- Windows with Autodesk Revit **2024** (.NET Framework 4.8), **2025**/**2026** (.NET 8)
  or **2027** (.NET 10)
- [.NET SDK 8](https://dotnet.microsoft.com/download) to build (SDK 10 for Revit 2027)
- Manifold mode: the `Revit template 2026.rfa` template (pipe types and families,
  including the Enddeckel caps)
- Optional Claude mode: set the `ANTHROPIC_API_KEY` environment variable before
  launching Revit

## Installation

```powershell
git clone <repo> sayRevit
cd sayRevit
.\scripts\install.ps1 -RevitVersion 2026
```

The script detects the installed Revit runtime, builds against its API assemblies and
copies the add-in to `%APPDATA%\Autodesk\Revit\Addins\<version>`. Restart Revit: the
**sayRevit** ribbon tab appears. Remove with `.\scripts\uninstall.ps1 -RevitVersion 2026`.

## Repository layout

```
src/SayRevit.Core     intent model (MepPlan, ManifoldPlan), IT/EN rule-based parser, preview formatter  [netstandard2.0]
src/SayRevit.Claude   Claude parser with structured output                                              [netstandard2.0, Anthropic SDK]
src/SayRevit.Addin    Revit add-in: ribbon, WPF UI, model catalog reader, element builder               [net48 / net8.0-windows / net10.0-windows]
tests/                xunit tests (68), OS-independent
scripts/              install.ps1 / uninstall.ps1
```

## Notes and limitations

- Straight runs with orthogonal branches only; no slopes, insulation or fixtures.
- Fittings (tees, takeoffs, elbows, caps) come from the routing preferences and loaded
  families of the selected type; anything missing is reported, never silently skipped.
- Requested sizes are validated against the type's available sizes; the nearest one is
  used and reported when there is no exact match.
- Always review the preview before creating; results depend on the families configured
  in each project.
