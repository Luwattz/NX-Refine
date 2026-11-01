# Faster hole search with batched surface sampling

This revision preserves the 29-group / 64-face housing expectation and the
[rounded rib-end exclusions](FILL_HOLES_RIB_END_EXCLUSION.md).

## Changes

`FaceSurfaceSampler` evaluates the existing three preliminary and eighteen
remaining fitting samples with NX `Evalsf.EvaluateArray`, requesting positions
and unit normals instead of unused principal curvature radii. Both parameter
directions, all 21 grid points, profile residuals, radius limits, inward-wall
checks and bore-wall requirements remain unchanged.

The evaluator uses `Initialize2`, whose face parameter mapping handles trimmed
B-surfaces and blends. A reference `AskFaceProps` result calibrates the face's
normal orientation and checks the first position/normal. Nonfinite data,
singular derivatives, zero normals, mismatched mapping or API failure cause
the evaluator to use the original per-point API. A native evaluator is reused
only within one face fit and always released before any geometry modification.
All NX calls remain on the NX thread.

Read-only face types, face edges and edge neighbors are cached only within a
single body scan. The cache is cleared on normal completion, stop or failure;
deletion and later scans read current topology again. A complete two-face
rounded cap/blend region without a real bore wall is rejected before the
expensive native whole-body recognition call, using the established exclusion.

## Numerical verification

The native `surface-samples` diagnostic compares both 21-point parameter
directions and the actual production three/remaining-eighteen call sequence.
On 110 test faces, including the ten protected rib-end faces, the production
sampler matched the previous API within 0.0000001 part units for positions and
0.0000001 for unit normal vectors.

The raw batch interface returned an opposite normal orientation on 52 faces;
the per-face calibration corrects that sign. One raw face evaluation returned
a zero normal at a collapsed parameter direction where `AskFaceProps` returned
the limiting normal. The production sampler detected the singularity and
used the old API, so the equivalence test passed there too.

Report:
`artifacts/cleanup-diagnosis-09c8e2c01c4a4c19a526fb800d7374a1/report.txt`.

## Native search and shape regressions

The previous deployed 29-group scan took 135320 ms. Batched sampling alone
took 85100 ms, retaining every exact group and negative assertion. The seed
phase fell from 53194 ms to 6865 ms. These are isolated NX batch measurements;
startup and highlight rendering are excluded, and timings vary with load.

The final build, including the early rounded-end rejection, took **73244 ms**
(about **46% less time** than the previous deployed version). All 29 exact
groups / 64 faces and negative assertions passed. The conical fixture retained
its exact complete group. The 958-face fixture retained all 11 current groups,
not only the nine required baseline groups.

- Final full scan:
  `artifacts/cleanup-diagnosis-83c0c6ec04084b85a80f92ef142799f6/report.txt`.
- Final conical check:
  `artifacts/cleanup-diagnosis-e80d780daa4d4158bfc75d42a03ee918/report.txt`.

- Previous result:
  `artifacts/cleanup-diagnosis-ff2eb7e931c943919aa61f241473ff40/report.txt`.
- Initial batched scan:
  `artifacts/cleanup-diagnosis-ecc550fc440342fdbad765e21f02ece4/report.txt`.
- With topology caching:
  `artifacts/cleanup-diagnosis-b589dd14cd6a4fdab2ec4e8635a592c1/report.txt`.
- Actual filling with the new evaluator:
  `artifacts/fill-holes-apply-9e1965db3a084d6780298f1205e336ba/report.txt`;
  eight valid groups filled, five rounded ends rejected, all ten protected
  faces and edge samples unchanged, body consistency, Cancel and undo passed.
- Older fixture:
  `artifacts/cleanup-diagnosis-67a03120eb3342e7ab01e8a138b2f766/report.txt`.

Run the sampling comparison using the compiled Framework/NX loader:

```powershell
.\scripts\diagnose-nx-cleanup.ps1 -PartPath C:\Users\chimp\Desktop\Geometry\housing.prt -Mode surface-samples -MaximumGroups 100
.\scripts\diagnose-nx-cleanup.ps1 -PartPath C:\Users\chimp\Desktop\Geometry\housing.prt -Mode holes-scan -ProbePath tests\data\housing-hole-coverage.txt
```
