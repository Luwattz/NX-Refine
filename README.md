# NX Refine

NX Refine is an open-source Siemens NX add-on for geometry validation, defeaturing, and simulation-oriented model cleanup. It adds a dedicated **Cleanup** ribbon tab to NX and wraps native NXOpen and UFUN operations in a preview-first workflow.

> Status: early functional prototype targeting Siemens NX 2512 on Windows. Always work on a copy of production geometry and validate repaired bodies before downstream use.

## Ribbon commands

| Group | Command | Current behavior |
|---|---|---|
| Simplify | Remove Blends | Filters seed faces by radius, expands native connected fillets, and retries native recognition options to delete healable chains. |
| Simplify | Fill Holes | Opens a native preview dialog with NX Hole Faces recognition, full-angle inner-wall validation, a maximum hole radius, and a reviewable candidate-face collector. |
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
- Siemens NX 2512 for the supplied build defaults
- NXOpen .NET assemblies from the target NX installation
- .NET Framework 4.x build tools or Visual Studio 2022
- Appropriate Siemens NX licenses for the modeling commands used

The default installation directory is `C:\Program Files\Siemens\DesigncenterNX2512`. Pass `-NXInstallDir` if NX 2512 is installed elsewhere. NXOpen binary compatibility is release-dependent; build the DLL against the deployment release.

## Build

Open PowerShell in the repository and run:

```powershell
.\scripts\build.ps1 -Configuration Release -NXInstallDir "C:\Program Files\Siemens\DesigncenterNX2512"
```

The build copies `NXRefine.dll` to `deploy\application`.

On a computer where `.ps1` files are blocked, run `scripts\update-nxrefine-nx2512.cmd` from Command Prompt or double-click it. It builds and registers NX Refine for the current user without administrator rights. It detects NX from the current NX environment, NX 2512 registry entries, or common installation directories on mounted drive letters (including D:). To choose an installation explicitly, run `scripts\update-nxrefine-nx2512.cmd "D:\Program Files\Siemens\DesigncenterNX2512"` or set `NX_INSTALL_DIR`. The window pauses on completion or failure; set `NXREFINE_NO_PAUSE=1` for unattended use.

## Install the ribbon

NX loads custom applications from directories listed in the file referenced by `UGII_CUSTOM_DIRECTORY_FILE`.

See [Installing on Siemens NX 2512](docs/INSTALL_NX2512.md) for the complete build, registration, verification, and uninstall procedure.

1. Build the project.
2. Add the absolute `deploy` directory to your NX custom directory file, one directory per line. `deploy\custom_dirs.dat.example` shows the expected format.
3. Alternatively run:

   ```powershell
   .\scripts\install-local.ps1 -CustomDirectoryFile "C:\NXCustom\custom_dirs.dat"
   ```

4. The custom BMP icons are stored directly in `deploy\application` beside the DLLs so NX can resolve them from the custom application root. No separate bitmap path is required; the included `.cmd` updater also registers this application directory in the current user's `UGII_BITMAP_PATH` as a fallback.
5. Set `UGII_CUSTOM_DIRECTORY_FILE` to that file if your NX environment does not already define it.
6. Restart NX and enter the Modeling application.
7. If the tab is hidden by the active role, right-click the ribbon and enable **Cleanup**.

The deployment layout follows the standard NX custom application convention:

```text
deploy/
├── application/
│   ├── NXRefine.dll
│   ├── NXRefine.*.dll
│   └── nxrefine_*.{sc,lc,2s,2l,8s}.bmp
└── startup/
    ├── nxrefine.men
    └── nxrefine_main.rtb
```

## Usage

1. Open a part and save a disposable copy.
2. Choose one focused repair from **Cleanup**.
3. Review highlighted candidates before applying the change.
4. Adjust thresholds in the command's native dialog or in **Settings** where applicable.
5. Inspect and validate the result before exporting to a simulation system.

Area and other cleanup thresholds use the current part unit. Remove Markings **Max feature height** is the exception: it is always entered in millimetres, including for inch parts.
Candidate faces are highlighted during repair preview. Repair completion counts are written silently to the NX system log; repair completion and cancellation do not open the Listing Window.

### Remove Blends workflow

Remove Blends uses an NX native Block Styler dialog with OK / Apply / Cancel navigation.

1. Select one or more solid bodies in **Target entities**.
2. Enter **Minimum seed radius** and **Maximum seed radius** in part units. Radius is a weak control: it filters only the initial seed faces. Native Connected Blend Faces expansion may include faces outside these limits, and those faces may be deleted too. Values are saved in `%APPDATA%\NXRefine\settings.ini`.
3. Radius edits remain provisional until committed; invalid input disables Apply / OK. Changing the range rebuilds the expanded preview, reusing cached queries on unchanged geometry. Preview reuses covered native connected components, individually checks spherical sources, and exhaustively checks regions where unequal overlapping rules expand coverage.
4. Review **Connected blend faces**. The preview contains full native rule results, including expansion beyond the seed radius. Manually deselected faces are protected: a rule containing one is skipped as a whole, rather than clipped.
5. **Apply** or **OK** uses live NX **Connected Blend Faces** rules directly in **Delete Face with Heal**. Expanded recognition is preferred; failed sources queue the remaining native recognition combinations after fresh sources. Rules are re-evaluated on current topology. No fixed face lists or arbitrary chain splitting are used.
6. Each native operation commits or rolls back independently. Complete selected-chain removal, solid preservation and absence of additional NX body-consistency errors are checked. The conservative whole-region rollback and local shape veto were reverted at the user's request; these checks do not guarantee every visual shape detail.
7. Deletion recognizes directly in the live delete builder and resolves current topology after each commit. Every original source stays queued. Small undo-protected transactions share expensive whole-body checks; a failed transaction rolls back and replays with individual checks. Interactive cleanup continues until the queue completes or **Stop** is requested, without the old 120-second / 256-attempt cutoff. **Stop** waits for the current NX call and pending validation, preserves accepted edits and the reviewed selection; **Apply** resumes without another Find. The progress window shows scan stages, counts, a progress bar and elapsed time that keeps updating during native calls. Temporary native rule wrappers are explicitly disposed after use. See [progress and continuous cleanup](docs/REMOVE_BLENDS_PROGRESS.md) and [deletion runtime](docs/REMOVE_BLENDS_RUNTIME.md).

Keep `deploy/application/NXRefine.RemoveBlends.dlx` beside `NXRefine.dll`; this is the native dialog layout required at runtime.

### Remove Markings workflow

The command uses an NX native Block Styler dialog with one merged face collector and OK / Apply / Cancel navigation. Labels remain English; NX supplies its own theme and navigation button language.

1. Use **Carrier faces and candidates** to select one or more lettering carrier faces. Each selected carrier's solid body becomes part of the search scope. The default selection intent is **Single Face**; NX's **Boss and Pocket Faces** intent remains available for review.
2. After every new carrier selection, its qualifying connected **boss / pocket groups** are highlighted and added to the same collector. Carrier faces remain stored in the collector but are deliberately not highlighted; the plugin tracks them separately and never sends them to Delete Face. The dialog has no additional status/explanation label below the controls.
3. Adjust **Max feature height (mm)** (default `2`) to reject tall bosses. The last valid value is saved in `%APPDATA%\NXRefine\settings.ini` and restored the next time the command opens, including after Apply or OK. The value is always interpreted in millimetres and converted to the active part unit system for geometry calculations. While editing, the text remains provisional and old candidates are cleared; no geometry scan starts until you press Enter, leave the field, or click Find / Apply / OK. Invalid or incomplete input leaves no deletion candidates and keeps Apply / OK unavailable. Character width and overall footprint are not limited.
4. To exclude a connected candidate, deselect any of its faces in the merged collector; the complete group is removed. Deselecting a carrier removes it from the search scope and rebuilds candidates for the remaining carriers. **Find Candidates / Restore All** rescans every current carrier and restores all qualifying candidates.
5. **Apply** deletes and heals only the retained candidate faces under one NX undo mark and leaves the dialog open. **OK** applies and closes. **Cancel** or Escape closes and removes the pending preview. Previously applied passes remain until NX Undo is used.

Keep `deploy/application/NXRefine.Markings.dlx` beside `NXRefine.dll`; this is the native dialog layout required at runtime.

Detection groups selected carriers by solid body, removes all selected carrier faces from that body's face-adjacency graph, and finds connected faces attached to any carrier. The support component reached through a carrier's exterior boundary loop is excluded, including on bodies thinner than the maximum marking height. Heights use a surface point and normal evaluated near each group, so analytic, B-surface, and offset-surface carriers do not depend on the WCS. A group touching multiple carriers qualifies when its projected boss/pocket height does not exceed the maximum relative to at least one attached carrier, and it is not the entire remaining body. Candidate groups are deduplicated per body. This is a geometric candidate search, not text recognition: shallow holes, bosses, ribs, and other details can qualify and must be excluded during review. Connected lettering that merges with another structure or lettering lacking a separate topological island may be missed. Assembly occurrences and sheet bodies are not supported by this workflow. Failed healing rolls back the pass and requires a fresh selection and scan.

### Fill Holes workflow

Fill Holes uses an NX native Block Styler dialog with OK / Apply / Cancel navigation.

1. Select one or more solid bodies in **Target entities**. Complete simple walls and fully fitted imported bores use a local closed-rim check; complex transitions and multistep walls use NX's native **Hole Faces** rule. A bounded search recognizes split walls only when their combined boundary closes around an inward bore axis. There is no manual seed selector.
2. Enter **Max hole radius (part units)**. A value of `0` means no radius limit. A positive value limits the largest cylindrical or conical radius in each accepted region, including entrance chamfers.
3. Review the single **Hole faces to fill** collector. Only pending faces are highlighted; the selected body is not highlighted as a whole. Deselecting any face excludes its entire hole group. Changing bodies rebuilds immediately; a radius edit stays provisional until Enter, focus leaves the field, or Apply/OK is pressed, so an incomplete value cannot start a scan.
4. **Apply** fills and leaves the dialog open; **OK** fills and closes. Up to four disjoint groups share one native Face/Heal commit and undo mark. A failed batch is undone before retrying each group separately, with specialized `Hole` mode as a fallback. Groups that neither mode can heal are skipped without undoing successes. If any group succeeds, the action is accepted even when others are skipped.
5. During deletion, the progress window shows completed groups and a **Stop** button. Stop waits for the current native operation to return, preserves completed fills, and leaves untouched reviewed groups selected. After an interrupted **Apply**, click Apply again to continue without another recognition pass. An accepted **OK** closes the dialog. **Cancel** clears the pending preview while preserving accepted fills.

Keep `deploy/application/NXRefine.FillHoles.dlx` beside `NXRefine.dll`; this is the native dialog layout required at runtime. Boss/Pocket expansion is not used because it can include ribs and carrier faces. The imported-wall fallback fits inward revolved surfaces, follows a local face region, and requires one or two closed rim loops winding once around the bore axis. Blind-hole disks must be bounded entirely by that region. It excludes incompatible axes and caps growth at twice the seed radius, as well as the requested radius. Large steps, interrupted walls and complex blended transitions may still be missed. Recognition does not guarantee that NX can heal the selected faces. A committed operation is checked for actual removal of all reviewed faces, preserved solid bodies and no increase in body consistency faults.

A circular cap with a rounded rib end is not sufficient evidence of a hole. Every imported candidate must also contain a proven inward cylindrical/conical bore wall, analytic or fitted. The same requirement is checked for each reviewed group before deletion, including groups supplied from an older preview. See [rib-end exclusion](docs/FILL_HOLES_RIB_END_EXCLUSION.md).

The primary pass rejects partial-angle, exterior and oversized seeds before expensive native recognition. Curve samples are reused within a body scan. Complete fitted regions avoid redundant native queries; uncertain regions query NX once. Previously recognized groups are not processed again by the imported-wall pass. The NX log records phase timings, rule counts and deletion/stop totals. See [housing hole coverage](docs/HOUSING_FILL_HOLES.md) and [performance and Stop regression](docs/FILL_HOLES_PERFORMANCE.md) for validation scope.

Surface fitting uses batched position/normal evaluation with per-face orientation calibration and legacy fallback at singular points. Read-only topology is cached for one scan only. See [sampling performance](docs/FILL_HOLES_BATCH_SAMPLING.md) for numerical equivalence and runtime checks.

### Clear Cavities workflow

Clear Cavities uses an NX native Block Styler dialog with OK / Apply / Cancel navigation.

1. Select one or more solid bodies in **Target entities**. The command uses NX's exterior-face ray classification and face-edge connectivity to find closed internal shells that do not connect to the outside. Open pockets and the body's exterior shell are excluded.
2. Review the **Cavity faces to remove** collector. Detected cavity shells are highlighted before any edit. All detected groups are initially retained; deselecting any face in a connected group removes that complete group from the preview and from the pending operation.
3. **Apply** deletes and heals only the retained cavity faces and leaves the dialog open; **OK** applies and closes. **Cancel** or Escape clears the preview without changing geometry. Each pass is protected by one NX undo mark.

Detection reads NX's native BREP shell topology in one call per body. NX identifies the first shell as the exterior; remaining shells supply the enclosed cavity groups. This avoids the previous per-edge adjacency reconstruction, whole-body exterior ray scan, and per-shell point/ray queries. Unexpected topology or reported topology states stop detection without enabling deletion. Open pockets and through holes belong to the exterior shell and are retained. The command does not delete the selected body itself, and a failed healing pass is rolled back. See [Clear Cavities regression](docs/CLEAR_CAVITIES_REGRESSION.md) for validation and timing scope.

### Repair Unattached Faces workflow

Repair Unattached Faces uses an NX native Block Styler dialog with OK / Apply / Cancel navigation.

1. Select one or more solid bodies in **Target entities**. The command requires a positive local separation within **Maximum gap (part units)**, opposing outward normals, multiple non-collinear samples, and sampled empty space between the faces. Fully joined and zero-distance-only contacts are excluded; opposing planar faces can still qualify through an open local patch when another portion is attached. The initial value is `0.01`, and subsequent uses remember the last gap value. Uncertain measurements are skipped; this conservative sampling is not a proof that every gap will be found. While editing any numeric field, NX keeps the text provisional; press **Enter**, leave the field, or use an action button to commit it before a scan starts.
2. Review the **Unattached faces to repair** collector. Only the smaller problem-side faces are highlighted. Connected problem-side faces form groups; a shared large support face does not merge independent gaps. Deselecting any candidate face removes its complete group from the pending repair. Deliberate small clearances can still qualify and must be excluded manually.
3. **Apply** repairs retained groups and leaves the dialog open; **OK** repairs and closes. **Cancel** or Escape clears the preview. Separate solid bodies use NX's native solid Sew operation. Gaps between faces in the same body use native Delete Face with Heal on the smaller face, allowing surrounding faces to close the gap. Each pass is protected by one NX undo mark.

This is a geometric proximity detector, not an intent recognizer. Deliberate clearances, thin walls, and nearby faces from different design features can qualify and must be reviewed before Apply/OK. Scans use a three-dimensional box index, planar prefilters, interior planar projections and cached source projections to reduce repeated geometry queries. Adjusting the gap reuses unchanged face geometry and bounded distance measurements; body selection changes and part edits invalidate this cache. Candidates and empty-space checks are recalculated for the new tolerance. Very large selections or tolerances may require a narrower scope; the scan logs a warning if its spatially close-pair safety limit is reached. Preparation/search timings and query/cache counters are logged for performance comparison. A failed repair pass is rolled back.

## Known limitations

- Hole detection combines native complete-wall recognition with a bounded closed-rim fallback for imported or split walls. Complex interrupted walls may be missed and candidates require manual review.
- Small-face deletion is heuristic and may fail when adjacent surfaces cannot be extended safely.
- `Repair Sheets` currently operates on all sheet bodies in the work part.
- Patterned-face replacement and missing-face surface reconstruction require topology-aware algorithms planned for later releases. Clear Cavities currently targets fully enclosed face shells; open pockets remain available through Fill Holes or native Delete Face workflows. Repair Unattached Faces is intentionally conservative around non-manifold and deliberately clear geometry.
- The project does not redistribute Siemens NX assemblies or documentation.

See [Architecture](docs/ARCHITECTURE.md) for implementation details and the roadmap.

## License

NX Refine is licensed under the [MIT License](LICENSE). Siemens and NX are trademarks of Siemens AG. This project is independent and is not affiliated with or endorsed by Siemens.

