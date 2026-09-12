# NX Refine

NX Refine is an open-source Siemens NX add-on for geometry validation, defeaturing, and simulation-oriented model cleanup. It adds a dedicated **Geometry Cleanup** ribbon tab to NX and wraps native NXOpen and UFUN operations in a preview-first workflow.

> Status: early functional prototype targeting Siemens NX 2312 on Windows. Always work on a copy of production geometry and validate repaired bodies before downstream use.

## Ribbon commands

| Group | Command | Current behavior |
|---|---|---|
| Inspect | Analyze | Runs NX Examine Geometry checks, detects short edges, small faces, small blends, and cylindrical hole candidates, then highlights findings. |
| Simplify | Remove Blends | Recognizes blend faces and removes those with radius at or below the configured threshold. |
| Simplify | Fill Holes | Opens a native preview dialog for one or more target entities, an inner-hole seed selector using NX's Boss and Pocket Faces rule, a maximum hole radius, and a reviewable candidate-face collector. |
| Simplify | Remove Markings | Select one or more carrier faces, automatically add their connected candidate groups to the same collector, exclude groups, then Apply or OK to delete and heal retained candidates. Works without feature history; candidates require review. |
| Repair | Repair Sheets | Sews sheet bodies with the configured tolerance and optimizes output faces. |
| Repair | Patch Openings | Detects and highlights open sheet boundaries. Automatic surface reconstruction is preview-only in v0.1. |
| NX Refine | Settings | Configures face area, edge length, radius, diameter, sewing tolerance, and sharp-angle thresholds. |
| NX Refine | About | Shows version and safety information. |

## Checks

The analyzer enables the native NX checks for:

- tiny and misaligned objects;
- body data structures and consistency;
- face-to-face and face self-intersections;
- sheet boundaries and missing-face indicators;
- face smoothness, spikes, and cuts;
- edge smoothness and edge tolerances.

Additional configurable heuristics identify small-area faces, short edges, blend faces, and cylindrical hole candidates.

## Requirements

- Windows x64
- Siemens NX 2312 for the supplied build defaults
- NXOpen .NET assemblies from the target NX installation
- .NET Framework 4.x build tools or Visual Studio 2022
- Appropriate Siemens NX licenses for the modeling commands used

Other recent NX releases can be targeted by passing their installation directory during the build. NXOpen binary compatibility is release-dependent; build the DLL against the deployment release.

## Build

Open PowerShell in the repository and run:

```powershell
.\scripts\build.ps1 -Configuration Release -NXInstallDir "C:\Program Files\Siemens\NX2312"
```

The build copies `NXRefine.dll` to `deploy\application`.

## Install the ribbon

NX loads custom applications from directories listed in the file referenced by `UGII_CUSTOM_DIRECTORY_FILE`.

See [Installing on Siemens NX 2312](docs/INSTALL_NX2312.md) for the complete build, registration, verification, and uninstall procedure.

1. Build the project.
2. Add the absolute `deploy` directory to your NX custom directory file, one directory per line. `deploy\custom_dirs.dat.example` shows the expected format.
3. Alternatively run:

   ```powershell
   .\scripts\install-local.ps1 -CustomDirectoryFile "C:\NXCustom\custom_dirs.dat"
   ```

4. Set `UGII_CUSTOM_DIRECTORY_FILE` to that file if your NX environment does not already define it.
5. Restart NX and enter the Modeling application.
6. If the tab is hidden by the active role, right-click the ribbon and enable **Geometry Cleanup**.

The deployment layout follows the standard NX custom application convention:

```text
deploy/
├── application/
│   ├── NXRefine.dll
│   └── NXRefine.*.dll
└── startup/
    ├── nxrefine.men
    └── nxrefine_main.rtb
```

## Usage

1. Open a part and save a disposable copy.
2. Choose **Geometry Cleanup > Analyze**.
3. Review highlighted entities and the NX Listing Window summary.
4. Adjust thresholds in **Settings**.
5. Run one focused repair at a time and inspect the result.
6. Run **Analyze** again before exporting to a simulation system.

Area and other cleanup thresholds use the current part unit. Remove Markings **Max feature height** is the exception: it is always entered in millimetres, including for inch parts.
Candidate faces are highlighted during repair preview. Closing a confirmation dialog clears the preview before deletion starts. Repair completion counts are written silently to the NX system log; repair completion and cancellation do not open the Listing Window. The Analyze command still opens its requested analysis report.

### Remove Markings workflow

The command uses an NX native Block Styler dialog with one merged face collector and OK / Apply / Cancel navigation. Labels remain English; NX supplies its own theme and navigation button language.

1. Use **Carrier faces and candidates** to select one or more lettering carrier faces. Each selected carrier's solid body becomes part of the search scope. The default selection intent is **Single Face**; NX's **Boss and Pocket Faces** intent remains available for review.
2. After every new carrier selection, its qualifying connected **boss / pocket groups** are highlighted and added to the same collector. Carrier faces remain stored in the collector but are deliberately not highlighted; the plugin tracks them separately and never sends them to Delete Face. The dialog has no additional status/explanation label below the controls.
3. Adjust **Max feature height (mm)** (default `2`) to reject tall bosses. The last valid value is saved in `%APPDATA%\NXRefine\settings.ini` and restored the next time the command opens, including after Apply or OK. The value is always interpreted in millimetres and converted to the active part unit system for geometry calculations. The native text field reads edits immediately: old candidates are cleared as you type, and the preview rebuilds after a short pause without requiring Enter. Invalid or incomplete input leaves no deletion candidates. OK / Apply are unavailable while the preview is pending or invalid. Character width and overall footprint are not limited.
4. To exclude a connected candidate, deselect any of its faces in the merged collector; the complete group is removed. Deselecting a carrier removes it from the search scope and rebuilds candidates for the remaining carriers. **Find Candidates / Restore All** rescans every current carrier and restores all qualifying candidates.
5. **Apply** deletes and heals only the retained candidate faces under one NX undo mark and leaves the dialog open. **OK** applies and closes. **Cancel** or Escape closes and removes the pending preview. Previously applied passes remain until NX Undo is used.

Keep `deploy/application/NXRefine.Markings.dlx` beside `NXRefine.dll`; this is the native dialog layout required at runtime.

Detection groups selected carriers by solid body, removes all selected carrier faces from that body's face-adjacency graph, and finds connected faces attached to any carrier. A group touching multiple carriers qualifies when its projected boss/pocket height does not exceed the maximum relative to at least one attached carrier, and it is not the entire remaining body. Candidate groups are deduplicated per body. This is a geometric candidate search, not text recognition: shallow holes, bosses, ribs, and other details can qualify and must be excluded during review. Connected lettering that merges with another structure or lettering lacking a separate topological island may be missed. Assembly occurrences and sheet bodies are not supported by this workflow. Failed healing rolls back the pass and requires a fresh selection and scan.

### Fill Holes workflow

Fill Holes uses an NX native Block Styler dialog with OK / Apply / Cancel navigation.

1. Select one or more solid bodies in **Target entities**. The selection is limited to the work part and is used as the search scope; if no body is selected, the bodies of the seed faces are inferred.
2. In **Seed inner hole faces**, pick the clearly visible inside cylindrical ring/wall of each screw hole or counterbore. The selector uses NX's native **Boss and Pocket Faces** intent, so NX expands the selected seed into its corresponding feature region. The plugin keeps only inner-facing cavity geometry and rejects exterior bosses and housing walls.
3. Enter **Max hole radius (part units)**. A value of `0` means no radius limit; otherwise a selected hole region is accepted only when its inner cylindrical radii are at or below the value.
4. The resulting regions are highlighted and recorded in **Hole faces to fill**. The collector accepts multiple faces. Deselecting any face removes its complete connected candidate region from the pending operation; the seed selector remains available for adding another hole.
5. **Apply** fills the retained hole faces and leaves the dialog open for another pass. **OK** fills and closes. **Cancel** or Escape clears the preview without modifying the part. Each pass uses one NX undo mark and failed multi-body healing rolls back the whole pass.

Keep `deploy/application/NXRefine.FillHoles.dlx` beside `NXRefine.dll`; this is the native dialog layout required at runtime. Native Boss/Pocket expansion is combined with geometric inner-wall validation; imported bosses and partial cylinders may require manual deselection.

## Known limitations

- Hole detection starts only from an explicitly selected inner cylindrical seed and the native Boss and Pocket Faces expansion; imported bosses, partial cylinders, and unusual face orientations can still require manual review.
- Small-face deletion is heuristic and may fail when adjacent surfaces cannot be extended safely.
- `Repair Sheets` currently operates on all sheet bodies in the work part.
- Arbitrary cavity removal, patterned-face replacement, and missing-face surface reconstruction require topology-aware algorithms planned for later releases.
- The project does not redistribute Siemens NX assemblies or documentation.

See [Architecture](docs/ARCHITECTURE.md) for implementation details and the roadmap.

## License

NX Refine is licensed under the [MIT License](LICENSE). Siemens and NX are trademarks of Siemens AG. This project is independent and is not affiliated with or endorsed by Siemens.
