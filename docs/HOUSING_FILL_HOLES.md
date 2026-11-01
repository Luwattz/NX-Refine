# housing.prt Fill Holes investigation

The original 34-group result included five rounded rib ends that are not
holes. See [the correction and shape-preservation regression](FILL_HOLES_RIB_END_EXCLUSION.md).
The expected result is now 29 groups / 64 faces; the six original arrow holes
remain included. The historical verification below did not establish that
every filled closed region was semantically a bore.

Later scan/deletion performance changes and Stop support are recorded in
[Fill Holes performance](FILL_HOLES_PERFORMANCE.md). Deployment hashes and
timings below describe the earlier coverage-fix revision.

Model: `C:\Users\chimp\Desktop\Geometry\housing.prt`, SHA256
`ED737FAFEB2784064DCEA3527FB210010BD36561C52E85422CB3E3C7F943A816`.
NX 2512 loads one solid body with 6120 faces. All runs use disposable copies,
restore the outer undo mark, and close without saving the input.

## Why the six holes were missed

At maximum radius 4, the old complete-wall pass recognized 21 groups / 31 faces. It
required an analytic cylindrical/conical seed and a complete angular span on
every analytic wall. The screenshot's six holes do not meet those conditions:

| Screenshot location | Representative faces | Geometry |
| --- | --- | --- |
| Third upper bracket, left bore | 39267, 44280, floor 43393 | Split Parametric walls and entrance chamfer |
| Fourth upper bracket, left bore | 40689, 39973, floor 39115 | Parametric walls with an oblique elliptical floor |
| Fourth upper bracket, right bore | 40722, 40786, 42572 | Partial cylinder plus spline and narrow planar segments |
| Lower right bracket, right bore | 42544, 44333, 44855 | Partial cylinder plus spline and narrow planar segments |
| Upper small right-panel hole | 43733, 43407 | Two Parametric wall halves |
| Lower small right-panel hole | 42880, 40062 | Two Parametric wall halves |

Picking the apparent center of the two right bracket bores can hit unrelated
geometry behind the opening. Rim probes were used to identify their actual
walls. Inward partial cylinders alone are not evidence of a hole: corner
rounds can also satisfy that test.

The fourth upper bracket has another issue: its entrance edge curves disagree
at a shared vertex by about 0.000085 mm, exceeding the old 0.00004 mm join
threshold. The part distance tolerance is 0.01 mm. Rim joining now uses the
bounded part tolerance, independently of radius filtering. Periodic seams are
excluded from region boundaries using neighboring face membership.

## Recognition and commit changes

The primary native path remains conservative. A separate local fallback fits
an inward cylinder/cone profile to surface points and normals, checks that its
axis lies outside the solid, and traces adjacent walls. Compatible fitted axes,
a local radius envelope and closed single-winding rims are required. A planar
blind floor qualifies only when all its edges belong to the established rim;
annular carriers are excluded. Native Hole Faces can add members but cannot
replace those checks. Native recognition is queried once per region, because
imported spline rules often return only their own seed.

An intermediate fallback allowed tiny corner fillets to grow across a pocket.
Those candidates were rejected before deployment. Growth is now capped at
twice the observed seed radius and cannot cross a known incompatible wall
axis. The negative assertions include those pocket seeds and isolated rounds.
This is intentionally conservative for large counterbore steps or interrupted
and heavily blended bores.

After Delete Face commits, every requested face must be absent, body count
must be preserved, affected bodies must remain solid and their consistency
fault count must not increase. A failed attempt is undone before retrying Hole
mode. Existing partial-success callback behavior is preserved.

## Native verification

The bounded full scan recognized 34 groups / 74 faces in 181191 ms: the original 21 plus
13 additional groups. Its six screenshot groups match the exact face sets in
`tests/data/housing-hole-coverage.txt`. The rejected pocket/round seeds do not
appear. Full scan timings are model/session dependent; this remains an
expensive operation on this imported model.

The deployed release was rescanned with explicit positive and negative
assertions: 34 groups in 210229 ms, coverage assertions and undo both passed.
Report: `artifacts/cleanup-diagnosis-e3a0aef713f6404ab558942e4b2a3ab3/report.txt`.
Deployed DLL SHA256:
`01E8F6A97EEB250EDC2E3AB47F546A17268C564B88FF68CD57F48A280E2B41FF`.
Both `bin/Release` and `deploy/application` match the tested release; the old
DLL/PDB remain under `artifacts/housing-hole-coverage/baseline`. The original
Geometry/housing.prt hash is unchanged.

The screenshot groups were exercised through the production Apply callback:
6 groups / 24 reviewed faces removed, zero skipped. Each of six sampled bore
axis points changed from outside the solid to inside the solid. Cancel after
accepted Apply preserved the result. Solid/body consistency and outer undo
checks passed. These are batch callback tests, not interactive mouse events.

All 13 additional groups were then tested together through Apply: 13 groups /
43 faces removed, zero skipped, and all 13 empty bore samples became solid.
Cancel preservation, body consistency and undo passed. This also exercises
blind holes whose circular floor supplies the sampling axis when their
rounded wall cannot be fitted independently.

Recognition report:
`artifacts/cleanup-diagnosis-d51576ec7ff649a1bce3c955928967cd/report.txt`.
Six-hole geometry and callback report:
`artifacts/fill-holes-apply-8f4339d0d9654dacbe3edbaec5cc8814/report.txt`.
All-added-groups report:
`artifacts/fill-holes-apply-353352ade63a4c90bc6415c1caf04fb1/report.txt`.
Partial-success/all-failed callback report for the final build:
`artifacts/fill-holes-apply-e97cfac669bc4e2dabffbbb36564f217/report.txt`.
The conical fixture still yields its original one group; the existing native
Remove Blends collector regression also passes.

The separate 958-face regression model now recognizes 14 groups instead of
the historical 9. Its five additional closed groups were tested with the
release DLL: two heal, while three circular blind-hole groups fail both native
Face and Hole modes because neighboring faces cannot close the deleted area.
The three failures retain their original void samples, and the two successful
groups retain solid samples. Cancel, body consistency and undo all pass:
`artifacts/fill-holes-apply-33cabbc6b11a4c80be87307be202dfd8/report.txt`.
This is a native healing limitation, not a claim that every recognized group
will fill successfully.

Run coverage on an unchanged copy of the identified model:

```powershell
.\scripts\diagnose-nx-cleanup.ps1 -PartPath C:\Users\chimp\Desktop\Geometry\housing.prt -Mode holes-scan -ProbePath tests\data\housing-hole-coverage.txt
```

`test-nx-fill-holes-apply.ps1` accepts `-GroupsPath` containing one comma-separated
face-tag group per line and `-ExpectedRemovedGroups`. Exact all-success runs
also verify empty-to-solid bore samples. Tags are specific to the documented
model load; changed models must be diagnosed again.
