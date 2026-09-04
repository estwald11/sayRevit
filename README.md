# sayRevit

Autodesk Revit add-in that creates MEP piping from structured input, using the pipe types,
system types and levels already loaded in the project. Two modes:

- **Text mode** — describe pipes and ducts in Italian or English
  (*"una tubazione DN200 lunga 10 m con 3 stacchi DN15"*). A deterministic rule-based
  parser runs offline; optionally, Claude (official Anthropic SDK, structured output)
  interprets free-form sentences.
- **Manifold mode (Collettore)** — fully parametric supply/return manifold builder.
  No natural language: every value comes from explicit fields.

Both modes show a preview with notes and warnings before anything is created. **Create in
Revit** closes the window and asks for the start point in the model (Esc cancels); the level is
the active view's one and the elevation above it is the `DefaultElevationMm` setting (2500 mm).
Creation runs in a single transaction (one Undo step), and created elements are selected at
the end.

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
- **Automatic spacing** (default): before building, every valve assembly (body, flanges,
  lever) is mounted in a scratch area, its real extents measured and discarded; the spacing is
  then the smallest that avoids clashes between neighbouring stubs, including the return stubs
  interlaced at half a pitch, never below the value typed in the field. Reaching the other
  header cannot be fixed by spacing and is reported as a warning.
- **Valves on every stub**: an in-line valve is inserted on each supply and return stub.
  Ball valve up to a DN threshold (default DN32), butterfly (boax) above it — the threshold
  is an input, as are the two families (picked among the pipe accessories loaded in the
  project), the preferred PN and the distance of the valve centre from the **outer edge** of the
  header (default 150 mm; the outer diameter comes from the pipe type's segment sizes, so the
  value is independent of the header DN the formula picks). Each family has its own rotation
  about the pipe axis (ball valve default 0°, boax 90°). The type is chosen from
  the family's type names, which may be metric (`DN40_PN16_48013980`) or imperial
  (`1 1/2" Lever`); the exact type per DN is shown in the preview before anything is created.
  Valves are placed on a work plane that contains the stub axis (what Revit does when you
  place them from a section) so families that refuse to be tilted still come out aligned;
  the instance is created with the reference direction along the stub and the plane normal
  where the family's Z axis must go (that is how the roll is applied), then verified and,
  if needed, oriented in-plane. Every attempt is checked on the connector axis
  and a full mounting log is written to `%APPDATA%\sayRevit\diagnostica-valvole.txt`.
  The stub is cut to the faces of the pieces, so the chain pipe-valve-pipe is continuous
  and connected. Level-based families cannot be tilted onto a vertical stub (Revit silently
  turns the rotation into one about the vertical axis), so before building the valve and
  flange families are made **work plane-based** (and "Always vertical" cleared) in the
  family itself and reloaded into the project; the pieces are then created on a work plane
  containing the stub axis with the reference direction along the stub.
  Butterfly valves are rolled around the pipe axis (90 degrees by default: lever crosswise
  to the header; 0 puts it along the header) and are mounted between two **Flansch** flanges, picked
  by material like the end caps (stainless `ATZ_NEUTRAL_6_Flansch`, carbon steel
  `ATZ_C-STAHL-WELD_6_Flansch`); the flange disc is turned toward the valve and the collar
  toward the pipe, recognised from the flange geometry. Those families ship with part type
  **Pipe Flange**, which Revit manages itself: at every regeneration it compares connected
  flanges with the pipe type's routing preferences and, when those say "Flanges: None" (as for
  welded types), silently deletes them when the transaction is committed. So, together with the
  work-plane conversion, the add-in changes the part type of the flange families to
  "Undefined" and reloads them; they then stay ordinary fittings that Revit leaves alone.
  After the commit every created element is checked again and anything Revit removed or
  moved is reported as a warning.

## Requirements

- Windows with Autodesk Revit **2024** (.NET Framework 4.8), **2025**/**2026** (.NET 8)
  or **2027** (.NET 10)
- [.NET SDK 8](https://dotnet.microsoft.com/download) to build (SDK 10 for Revit 2027)
- Manifold mode: the `Revit template 2026.rfa` template (pipe types and families,
  including the Enddeckel caps and the valve families)
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

### Test bench (headless runs)

While Revit is open with a project, writing `%APPDATA%\sayRevit\automation\request.txt` (any
content, optionally `clean=tracked|all|none`, `build=none` to only clean up, `start=x,y` in mm to
build away from what is already there, and `Manifold*=` overrides of the saved settings)
makes the loader run the add-in without a window: the previous bench run is deleted, the
manifold is rebuilt from the saved settings, a PNG of the 3D view is exported to `view.png`
and the summary, notes and warnings go to `result.txt` next to it. Combined with the hot
update this gives a build-run-look loop without touching Revit.

### Updating with Revit open

Revit only loads `SayRevit.Loader.dll` directly. The ribbon button runs the loader, which reads
`SayRevit.Addin.dll` and its libraries from the install folder into memory on every click, so the
files on disk are never locked. Rerunning `install.ps1` while Revit is open builds and copies the
add-in in place; the next click runs the new code. A restart is only needed the first time (to
switch to the loader) or when the loader itself changes. On Revit 2025+ each run uses an
unloadable `AssemblyLoadContext`; on Revit 2024 (.NET Framework) old copies stay in memory.

## Repository layout

```
src/SayRevit.Core     intent model (MepPlan, ManifoldPlan), IT/EN rule-based parser, preview formatter  [netstandard2.0]
src/SayRevit.Claude   Claude parser with structured output                                              [netstandard2.0, Anthropic SDK]
src/SayRevit.Addin    Revit add-in: ribbon, WPF UI, model catalog reader, element builder               [net48 / net8.0-windows / net10.0-windows]
tests/                xunit tests (113), OS-independent
src/SayRevit.Loader/  hot loader: the only assembly Revit loads directly (see below)
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
