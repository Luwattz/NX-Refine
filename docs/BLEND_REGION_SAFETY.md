# Complete-region rollback and local shape validation

**Reverted at the user's request.** This document records the conservative
experiment, not the active implementation. Native per-chain retries and weak
seed-radius filtering are restored; see [NATIVE_BLEND_RETRIES.md](NATIVE_BLEND_RETRIES.md).

The user reported partial fillet loops and pointed extensions after native-rule
retries. Native deletion success and a consistent solid do not establish the
intended shape. This revision adds acceptance conditions to the native deletion
workflow; it does not replace NX healing.

## Region transactions

Before modifying geometry, recognize all four native option combinations from
the original seeds and expanded preview faces. Merge results that overlap or
share an edge between recognized faces. Do not add ordinary support faces to
the deletion list. These connected regions form rollback boundaries, not fixed
face lists submitted for deletion.

Process each region using live native rules. Keep its changes only when all
original member faces are gone and no other original region has become partly
removed. Failure, budget exhaustion, or exceptions restore the entire current
region, including its earlier successful suboperations. Rollback failure aborts
the operation. Regions that were completed independently remain committed.
Smaller regions are processed first so a difficult large network does not
consume the whole budget before independent regions are attempted.
Each region receives a share of the remaining time and attempt budget (minimum
3 seconds and 4 attempts, still subject to the global limit). An unfinished
region is restored when its share expires, allowing later regions to be tried.
Budget checks occur between NX calls; an individual kernel call can overrun a
deadline. The final message reports preserved regions and rejected extensions.

## Shape acceptance

Before each native delete and each region transaction, record affected body
edge vertices and individual selected-face bounding boxes. A new/moved vertex
must lie inside a selected-face box expanded by twice that face's recognized
radius (minimum 0.001 part units; unknown radius uses this minimum). Unchanged
vertices are allowed outside these boxes. Check both per-operation and
cumulative region change. Reject excessive extension even if NX reports a
consistent solid. The UI radius range remains a seed-only filter.

This deliberately conservative vertex/box test may reject legitimate remote
intersections. It is not a surface-deviation or curvature proof: distortion
inside the allowed boxes, including changes without moved edge vertices, is
not guaranteed to be detected. Likewise, region completeness uses original
face membership; it does not prove arbitrary geometry unchanged under a
retained face tag. The screenshots were not individually matched to face tags.

## Regression

- Synthetic NX journal verifies all native modes, weak radius, manual exclusion,
  normal healed deletion and undo. It also creates a tall union extension that
  remains a solid and asserts that the locality guard rejects it.
- The housing benchmark constructs the original production regions before each
  invocation, verifies that every region has all or none of its face tags
  removed, checks actual/reported counts and solids, and restores the complete
  input with undo after repeated invocations.
- No input part is saved. Previously damaged geometry must be undone or replaced
  with an undamaged source; this command does not reconstruct earlier shapes.

Run:

```powershell
./scripts/test-nx-remove-blends.ps1 -PartPath ./artifacts/blend-bench/model_baseline.prt -MinimumRemoved 1 -RepeatCount 2
```

Deletion-count thresholds from the less restrictive retry policy are not
acceptance targets for this safety revision.

Final housing regression (region budget enabled):

| Round | Preview | Removed / reported | Attempts | Time (ms) | Regions checked |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 524 | 28 / 28 | 43 | 120037 | 33 |
| 2 | 496 | 0 / 0 | 36 | 140958 | 18 |

Both rounds reached the global budget and passed region atomicity, current-face
count and solid checks. Full undo restored the original topology. The second
round includes a kernel-call overrun of the nominal 120-second limit; budget
checks cannot interrupt an NX call. Remaining faces are not all proven
unremovable. Evidence: `artifacts/blend-bench/region-safety-fair-budget.log` and
`region-safety-synthetic.log`. The earlier no-share run (14, then 0 removals)
also passed these checks, but is not the deployed scheduling policy.
