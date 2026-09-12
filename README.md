# NX Refine

NX Refine is an open-source Siemens NX add-on for geometry validation, defeaturing, and simulation-oriented model cleanup. It adds a dedicated **Geometry Cleanup** ribbon tab to NX and wraps native NXOpen and UFUN operations in a preview-first workflow.

> Status: early functional prototype targeting Siemens NX 2312 on Windows. Always work on a copy of production geometry and validate repaired bodies before downstream use.

## Ribbon commands

| Group | Command | Current behavior |
|---|---|---|
| Inspect | Analyze | Runs NX Examine Geometry checks, detects short edges, small faces, small blends, and cylindrical hole candidates, then highlights findings. |
| Simplify | Remove Blends | Recognizes blend faces and removes those with radius at or below the configured threshold. |
| Simplify | Fill Holes | Finds cylindrical candidates below the configured diameter and invokes NX hole deletion/healing. |
| Simplify | Remove Markings | Select a body and carrier face, find small connected geometry groups, exclude groups, then Apply or OK to delete and heal checked faces. Works without feature history; candidates require review. |
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

All length values use the current part unit. Area values use the corresponding squared part unit. The default maximum marking height `2.0` therefore means millimetres in a metric part; for an inch part, enter approximately `0.0787`.
Candidate faces are highlighted during repair preview. Closing a confirmation dialog clears the preview before deletion starts. Repair completion counts are written silently to the NX system log; repair completion and cancellation do not open the Listing Window. The Analyze command still opens its requested analysis report.

### Remove Markings workflow

The command uses an NX native Block Styler dialog with native selection collectors and OK / Apply / Cancel navigation. Labels remain English; NX supplies its own theme and navigation button language.

1. Select exactly one large lettering carrier face in **Carrier face**. Its solid body becomes the search scope.
2. Candidates are treated as connected **boss / pocket groups**, so the faces making up one character stay together. Adjust **Max feature height** (default `2`, current part units) to reject tall bosses. The native text field reads edits immediately: old candidates are cleared as you type, and the preview rebuilds after a short pause without requiring Enter. Invalid or incomplete input leaves no deletion candidates. OK / Apply are unavailable while the preview is pending or invalid. Character width and overall footprint are not limited. Changing height or clicking **Find Candidates / Restore All** rescans and restores every candidate.
3. **Boss / pocket faces to delete** holds the highlighted candidates. Activate this native collector and use NX's deselection controls to remove a face; its entire connected group is excluded. The carrier face is never a deletion candidate.
4. **Apply** deletes and heals the retained faces under one NX undo mark and leaves the dialog open. Select a carrier again for another pass. **OK** applies and closes. **Cancel** or Escape closes and removes the pending preview. Previously applied passes remain until NX Undo is used.

Keep `deploy/application/NXRefine.Markings.dlx` beside `NXRefine.dll`; this is the native dialog layout required at runtime.

Detection removes the carrier face from the face-adjacency graph and groups connected faces attached to it. A group qualifies when its projected boss/pocket height does not exceed the maximum and it is not the entire remaining body. This is a geometric candidate search, not text recognition: shallow holes, bosses, ribs, and other details can qualify and must be excluded during review. Connected lettering that merges with another structure, lettering spanning multiple carrier faces, or lettering lacking a separate topological island may be missed. Assembly occurrences and sheet bodies are not supported by this workflow. Failed healing rolls back the pass and requires a fresh selection and scan.

## Known limitations

- A cylindrical face is only a hole candidate; imported bosses and partial cylinders can require manual review.
- Small-face deletion is heuristic and may fail when adjacent surfaces cannot be extended safely.
- `Repair Sheets` currently operates on all sheet bodies in the work part.
- Arbitrary cavity removal, patterned-face replacement, and missing-face surface reconstruction require topology-aware algorithms planned for later releases.
- The project does not redistribute Siemens NX assemblies or documentation.

See [Architecture](docs/ARCHITECTURE.md) for implementation details and the roadmap.

## License

NX Refine is licensed under the [MIT License](LICENSE). Siemens and NX are trademarks of Siemens AG. This project is independent and is not affiliated with or endorsed by Siemens.
