# Clear Cavities native topology regression

The detector uses `UF_BREP_ask_topology` and releases the returned tree in a
`finally` block with `UF_BREP_release_topology`. The installed NX 2512 SDK's
`UGOPEN/uf_brep_types.h` documents the solid's first child as its outer shell;
the remaining shell children describe the enclosed void boundaries. Shell face
tags are checked for uniqueness, membership in the selected body, and complete
coverage of that body's faces. Native errors, topology states, or unexpected
tree structure abort detection. This is not a general geometry-validity checker
for corrupt or self-intersecting imported models.

The old implementation rebuilt adjacency using each edge's incident faces,
called `IdentifyExteriorUsingRays` on the entire body, then sampled candidate
shells using point containment and ray tracing. The new implementation reads
the kernel's shell classification directly. It also handles single-face shells
without needing shared edges or numerical offset tolerances.

## Running the regression

Build Release, then run `scripts/test-nx-clear-cavities.ps1`. Optionally supply
`-BaselineAssembly` pointing to a separately named DLL built from the previous
implementation for candidate parity and performance comparison. Tests execute
in a separate NX journal process and create unsaved synthetic parts only.
The journal asks NX to select the compiled DLL's Framework runtime to avoid
mixing .NET 8 and Framework NX wrappers.

Coverage:

- A plain block, an open pocket, and a through hole produce no cavity candidates.
- Independent box and single-face spherical cavities form two groups.
- Deleting one selected group preserves the other cavity and all exterior faces.
- A millimeter fixture contains 1,000 box cavities (6,006 total faces); an inch
  fixture contains 100 box cavities (606 total faces).
- Batch healing removes exactly the selected cavity faces and retains the solid
  bodies. Reanalysis finds no remaining cavities after full healing.
- Undo restores original face tags and cavity groups.
- When a baseline DLL is supplied, candidate face sets must match exactly.

Recognition timings use the median of five runs after warm-up, including the
production detector's logging. Healing is timed separately. These measurements
exclude fixture construction, NX startup, and interactive selection/highlighting;
they do not promise the same elapsed time for arbitrary customer models.

## Results (2026-09-17, NX 2512)

All automated checks passed. The baseline was compiled from the previous
`ClearCavitiesDialog.cs` in Git HEAD into a separately named assembly. Both
implementations ran against the same bodies in the same NX process.

| Fixture | Previous detection median | Native detection median | Native-path healing |
| --- | ---: | ---: | ---: |
| 1,000 cavities, 6,006 faces, millimeters | 14,583 ms | 61 ms | 6,848 ms / 6,000 removed faces |
| 100 cavities, 606 faces, inches | 108 ms | 4 ms | 143 ms / 600 removed faces |

Detection improved about 239 times on the larger synthetic fixture. The
remaining dominant cost is NX Delete Face healing; its algorithm is unchanged.
The raw run output is in `artifacts/cavities-regression-large.txt` (local,
ignored by Git). Release build succeeded with existing Framework reference-pack
and deprecated callback warnings. Interactive dialog checks below and customer
large-model timings remain manual validation items.

## Manual UI checklist (not covered by the journal)

Run on disposable copies in interactive NX 2512.

## Dialog and selection

- [ ] Cleanup contains **Clear Cavities** in the Simplify group and the command opens the native Block Styler dialog.
- [ ] Target entities accepts one or more solid bodies and rejects sheet bodies or assembly occurrences.
- [ ] The cavity collector is populated only after a body is selected; all detected cavity faces are highlighted and the selected body is not highlighted as a whole.
- [ ] A body with no fully enclosed cavity leaves the collector empty and does not show an error page.
- [ ] Deselecting any face in a connected cavity group removes the complete group from the preview; reselecting it restores the group.

## Detection

- [ ] A blind/open pocket connected to an exterior face is not offered as an enclosed cavity.
- [ ] A completely enclosed void bounded by a separate inner face shell is offered as one connected candidate group.
- [ ] Multiple bodies and multiple independent cavities are deduplicated and shown together.
- [ ] Imported/non-manifold or self-intersecting geometry is logged as a review case rather than silently treated as a cavity.

## Commit and cancellation

- [ ] **Apply** deletes and heals only retained cavity groups, keeps the dialog open, and refreshes the preview.
- [ ] **OK** performs the same edit and closes the dialog.
- [ ] **Cancel**, Escape, and closing the dialog clear highlights and leave geometry unchanged.
- [ ] A failed healing pass returns to the single NX undo mark created for that pass.
