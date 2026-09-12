# NX Refine

NX Refine is an open-source Siemens NX add-on for geometry validation, defeaturing, and simulation-oriented model cleanup. It adds a dedicated **Geometry Cleanup** ribbon tab to NX and wraps native NXOpen and UFUN operations in a preview-first workflow.

> Status: early functional prototype targeting Siemens NX 2312 on Windows. Always work on a copy of production geometry and validate repaired bodies before downstream use.

## Ribbon commands

| Group | Command | Current behavior |
|---|---|---|
| Inspect | Analyze | Runs NX Examine Geometry checks, detects short edges, small faces, small blends, and cylindrical hole candidates, then highlights findings. |
| Simplify | Remove Blends | Recognizes blend faces and removes those with radius at or below the configured threshold. |
| Simplify | Fill Holes | Opens a native preview dialog for one or more target entities, automatic inner-hole seeds expanded by NX's Boss and Pocket Faces rule, a maximum hole radius, and a reviewable candidate-face collector. |
| Simplify | Clear Cavities | Opens a native preview dialog for one or more solid bodies, detects face shells that are fully enclosed and disconnected from the outside, and lets you review or exclude cavity groups before healing them. |
| Repair | Repair Unattached Faces | Opens a native preview dialog for one or more solid bodies, finds non-adjacent faces whose minimum separation is within the selected gap tolerance, and repairs retained gaps with native Sew or Delete Face/Heal operations. |
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
3. Adjust **Max feature height (mm)** (default `2`) to reject tall bosses. The last valid value is saved in `%APPDATA%\NXRefine\settings.ini` and restored the next time the command opens, including after Apply or OK. The value is always interpreted in millimetres and converted to the active part unit system for geometry calculations. While editing, the text remains provisional and old candidates are cleared; no geometry scan starts until you press Enter, leave the field, or click Find / Apply / OK. Invalid or incomplete input leaves no deletion candidates and keeps Apply / OK unavailable. Character width and overall footprint are not limited.
4. To exclude a connected candidate, deselect any of its faces in the merged collector; the complete group is removed. Deselecting a carrier removes it from the search scope and rebuilds candidates for the remaining carriers. **Find Candidates / Restore All** rescans every current carrier and restores all qualifying candidates.
5. **Apply** deletes and heals only the retained candidate faces under one NX undo mark and leaves the dialog open. **OK** applies and closes. **Cancel** or Escape closes and removes the pending preview. Previously applied passes remain until NX Undo is used.

Keep `deploy/application/NXRefine.Markings.dlx` beside `NXRefine.dll`; this is the native dialog layout required at runtime.

Detection groups selected carriers by solid body, removes all selected carrier faces from that body's face-adjacency graph, and finds connected faces attached to any carrier. A group touching multiple carriers qualifies when its projected boss/pocket height does not exceed the maximum relative to at least one attached carrier, and it is not the entire remaining body. Candidate groups are deduplicated per body. This is a geometric candidate search, not text recognition: shallow holes, bosses, ribs, and other details can qualify and must be excluded during review. Connected lettering that merges with another structure or lettering lacking a separate topological island may be missed. Assembly occurrences and sheet bodies are not supported by this workflow. Failed healing rolls back the pass and requires a fresh selection and scan.

### Fill Holes workflow

Fill Holes uses an NX native Block Styler dialog with OK / Apply / Cancel navigation.

1. Select one or more solid bodies in **Target entities**. Inner cylindrical walls are detected automatically and expanded by NX's native **Boss and Pocket Faces** rule. There is no manual seed selector.
2. Enter **Max hole radius (part units)**. A value of `0` means no radius limit. A positive value limits all inner cylindrical radii in each accepted region.
3. Review the single **Hole faces to fill** collector. Only pending faces are highlighted; the selected body is not highlighted as a whole. Deselecting any face excludes its entire hole group. Changing bodies rebuilds immediately; a radius edit stays provisional until Enter, focus leaves the field, or Apply/OK is pressed, so an incomplete value cannot start a scan.
4. **Apply** fills the retained faces and leaves the dialog open; **OK** fills and closes. **Cancel** clears the preview. Each pass uses one NX undo mark, and failed multi-body healing rolls back the pass.

Keep `deploy/application/NXRefine.FillHoles.dlx` beside `NXRefine.dll`; this is the native dialog layout required at runtime. Native Boss/Pocket expansion is combined with geometric inner-wall validation. There is no adjacency-flood fallback: failed native expansions are skipped and logged. All cylindrical walls in a region must be internal and coaxial, preventing rounded rectangular cavities from qualifying as circular holes. Regions containing exterior cylinders or the entire body are rejected; intersecting holes and unusual imported topology may require manual repair.

### Clear Cavities workflow

Clear Cavities uses an NX native Block Styler dialog with OK / Apply / Cancel navigation.

1. Select one or more solid bodies in **Target entities**. The command uses NX's exterior-face ray classification and face-edge connectivity to find closed internal shells that do not connect to the outside. Open pockets and the body's exterior shell are excluded.
2. Review the **Cavity faces to remove** collector. Detected cavity shells are highlighted before any edit. All detected groups are initially retained; deselecting any face in a connected group removes that complete group from the preview and from the pending operation.
3. **Apply** deletes and heals only the retained cavity faces and leaves the dialog open; **OK** applies and closes. **Cancel** or Escape clears the preview without changing geometry. Each pass is protected by one NX undo mark.

This is a topology-based enclosed-shell detector, not a semantic recognition system. Imported or non-manifold bodies, self-intersecting faces, and cavities represented by a single connected exterior component may need manual review. The command does not delete the selected body itself, and a failed healing pass is rolled back.

### Repair Unattached Faces workflow

Repair Unattached Faces uses an NX native Block Styler dialog with OK / Apply / Cancel navigation.

1. Select one or more solid bodies in **Target entities**. The command excludes shared edges and zero-distance contacts. It requires a positive separation below **Maximum gap (part units)**, opposing outward normals, multiple non-collinear samples, and sampled empty space between the faces. The initial value follows **Settings > Sew tolerance**. Uncertain measurements are skipped; this conservative sampling is not a proof that every gap will be found. While editing any numeric field, NX keeps the text provisional; press **Enter**, leave the field, or use an action button to commit it before a scan starts.
2. Review the **Unattached faces to repair** collector. Only the smaller problem-side faces are highlighted. Connected problem-side faces form groups; a shared large support face does not merge independent gaps. Deselecting any candidate face removes its complete group from the pending repair. Deliberate small clearances can still qualify and must be excluded manually.
3. **Apply** repairs retained groups and leaves the dialog open; **OK** repairs and closes. **Cancel** or Escape clears the preview. Separate solid bodies use NX's native solid Sew operation. Gaps between faces in the same body use native Delete Face with Heal on the smaller face, allowing surrounding faces to close the gap. Each pass is protected by one NX undo mark.

This is a geometric proximity detector, not an intent recognizer. Deliberate clearances, thin walls, and nearby faces from different design features can qualify and must be reviewed before Apply/OK. Very large selections or tolerances may require a narrower scope; the scan logs a warning if its close-pair safety limit is reached. A failed repair pass is rolled back.

## Known limitations

- Hole detection starts from automatically detected inner cylindrical seeds and evaluates native Boss and Pocket Faces rules; imported bosses, partial cylinders, and unusual face orientations can still require manual review.
- Small-face deletion is heuristic and may fail when adjacent surfaces cannot be extended safely.
- `Repair Sheets` currently operates on all sheet bodies in the work part.
- Patterned-face replacement and missing-face surface reconstruction require topology-aware algorithms planned for later releases. Clear Cavities currently targets fully enclosed face shells; open pockets remain available through Fill Holes or native Delete Face workflows. Repair Unattached Faces is intentionally conservative around non-manifold and deliberately clear geometry.
- The project does not redistribute Siemens NX assemblies or documentation.

See [Architecture](docs/ARCHITECTURE.md) for implementation details and the roadmap.

## License

NX Refine is licensed under the [MIT License](LICENSE). Siemens and NX are trademarks of Siemens AG. This project is independent and is not affiliated with or endorsed by Siemens.
