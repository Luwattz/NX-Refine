# Fill Holes performance and stopping

These timings describe the performance revision before
[rounded rib-end exclusion](FILL_HOLES_RIB_END_EXCLUSION.md). Five of its 34
candidate groups were subsequently identified as rib ends rather than holes;
the current expected selection is 29 groups / 64 faces. Successful healing,
solid consistency and a filled axis sample do not prove a feature is a hole.

## Scope

Use the unchanged 6120-face housing model identified in
[the coverage investigation](HOUSING_FILL_HOLES.md). All native tests use
copies, restore an outer undo mark and close without saving. These are batch
callback tests, not mouse-driven interaction tests.

## Changes

- Recognize a complete single analytic wall locally only after its caps and
  closed winding rims are established. Sample adjacent opening normals to
  distinguish an exterior opening from an unresolved floor or bottom fillet.
  Adjacent coaxial analytic walls still use native recognition so separately
  reviewed stages are not merged.
- A fully fitted spline region can avoid native rescanning when every wall
  passes the inward revolved profile/coaxial checks and every opening points
  away from the bore interior. Unfitted, faceted or ambiguous transitions
  continue through the native rule and existing safety checks.
- Cache 33-point rim samples for one body scan, then discard them before any
  model mutation. Exclude already recognized groups from further processing.
  Log seed fitting, region tracing and primary/native scan timings.
- Defer expensive solid containment tests until a closed candidate or a
  geometrically relevant incompatible neighbor needs them, and cache each
  seed's result. The original sampled axis point is retained. If both surface
  parameter directions fit, resolve their original ordered choice eagerly.
  Final region acceptance still requires the same void/inner-wall checks.
- Commit up to four disjoint reviewed groups together. Each batch is atomic:
  verify all requested faces are gone, bodies remain solid, and consistency
  faults do not increase. Roll a failed batch back before independent Face/
  Hole retries. Duplicate/stale groups retain the independent failure path.
- Reuse the latest validated body consistency baseline within Apply. Validate
  every successful commit, and discard the cache when Apply exits.

An early analytic prototype merged two pairs of coaxial stages. It was not
deployed. The regression now asserts all 34 exact face sets and the total
group count, in addition to excluding the known pocket/round seeds.

## Stop behavior

Apply now creates the same independently responsive progress window used by
recognition. Its Stop button sets only managed state; every NX call stays on
the NX thread. Check requests between batches, between independent retries,
and before committing an attempt. An active kernel call must return first;
it is not forcibly terminated from a worker thread.

A stop is handled as a normal partial result. Completed batches remain,
cancelled attempts are rolled back, and untouched reviewed groups are restored
to the collector without recognition. After Apply, another Apply processes
those retained groups. Cancel preserves accepted geometry. As with other
partial successes, OK is accepted if any group committed and may close the
dialog.

`SelectionScan` skips the visual window in NX batch sessions while retaining
the same checkpoint/request mechanism. `-StopAfterSuccess` in the Apply
regression sends the exact managed request used by the button from a worker
that never calls NX. It checks partial geometry, retained pending groups,
continuation without another scan, Cancel preservation and outer undo.

## Measured results

The previous release recognized the full housing in 210229 ms. The optimized
scan took 138453 ms (about 34% less), retaining all 34 exact groups / 74 faces
and excluding the same negative seeds. Native phase logging reports 47565 ms
for the primary pass, 61834 ms for composite seeds and 29038 ms for tracing.
Recognition remains expensive on this imported model; no latency target is
implied. The exact native diagnostic timing is recorded in the report below.

On the same 13 added groups / 43 faces:

| Build | Apply time | Healed groups | Bore samples filled |
| --- | ---: | ---: | ---: |
| Previous deployed DLL | 77781 ms | 13 | 13 |
| Optimized tested build | 16182 ms | 13 | 13 |

This run reduced measured Apply time by about 79%. Startup, recognition and
test sample preparation are excluded from Apply timing. Native call timings
vary with model/session load; this is not a constant latency guarantee.

Reports:

- Baseline: `artifacts/fill-holes-apply-36048b2c8d6646e38265d19886a6959e/report.txt`.
- Optimized: `artifacts/fill-holes-apply-7e2ab157f8fd43cbb7a71c0ef87ac100/report.txt`.
- Full recognition and negative assertions:
  `artifacts/cleanup-diagnosis-22e59dd044fc42a097ce2115049669f7/report.txt`.
- Stop and continuation:
  `artifacts/fill-holes-apply-7638f5e9fe4a4ce1a57973629423db33/report.txt`;
  eight completed groups stayed filled, five untouched groups stayed empty
  and selected, and a second Apply filled the remainder without recognition.
- Stale/duplicate/all-failed callback regression:
  `artifacts/fill-holes-apply-50c5a080a21943578f1ee0348c6fb5f0/report.txt`.
- Mixed native failures: `artifacts/fill-holes-apply-05c4cb02f08042faa607f08000f29323/report.txt`;
  two groups filled, three skipped, all five sample states correct, consistency
  and undo passed.

The older 958-face fixture retains all 14 group face sets, and the conical
fixture retains its one group. Their reports are respectively
`artifacts/cleanup-diagnosis-af2b155b9c2243b298e6169827492553/report.txt` and
`artifacts/cleanup-diagnosis-826822a2b7414ca1bb0eecac310fa179/report.txt`.

The tested build was copied to both `deploy/application` and `bin/Release`;
their DLL SHA256 is
`161760E926B544C3BDBB1A1B4B549B347C65F4B3994B390A68A6333F586538B0`.
The previous DLL/PDB are backed up in `artifacts/fill-holes-performance/baseline`.
The original Geometry/housing.prt SHA256 remains unchanged.

## Commands

```powershell
.\scripts\diagnose-nx-cleanup.ps1 -PartPath C:\Users\chimp\Desktop\Geometry\housing.prt -Mode holes-scan -ProbePath tests\data\housing-hole-coverage.txt
.\scripts\test-nx-fill-holes-apply.ps1 -PartPath C:\Users\chimp\Desktop\Geometry\housing.prt -BodyTag 38911 -AllReviewed -GroupsPath artifacts\housing-hole-coverage\all-added-groups.txt -ExpectedRemovedGroups 13
.\scripts\test-nx-fill-holes-apply.ps1 -PartPath C:\Users\chimp\Desktop\Geometry\housing.prt -BodyTag 38911 -AllReviewed -GroupsPath artifacts\housing-hole-coverage\all-added-groups.txt -StopAfterSuccess
```
