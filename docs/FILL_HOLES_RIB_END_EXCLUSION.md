# Excluding rounded rib ends from Fill Holes

The user's before/after images identify a small round rib end beside the lower
bracket boss in housing.prt. Removing it extends the supporting rib faces into
a pointed tip. Its faces are 41386 (planar circular cap) and 42536
(SurfaceOfRevolution blend). It is not a round bore.

The model/hash is the same as [the housing investigation](HOUSING_FILL_HOLES.md).
All diagnosis and deletion tests use unsaved copies.

## Cause

The circular-floor fallback accepted a closed winding boundary and an inward
region near its axis. A round cap plus a concave rounded transition can pass
those conditions. Native Hole Faces can return the floor seed alone, and
Delete Face/Heal can remove the rounded end successfully by extending the rib.
The resulting pointed geometry can still be a consistent solid and can fill
the selected empty axis sample. Those previous checks did not distinguish a
hole from a rounded rib termination.

The following five groups share this signature. Each contains a circular
planar cap and a noncylindrical revolved blend; none supplies a valid inward
cylinder/cone profile:

| Cap | Revolved blend |
| --- | --- |
| 39151 | 40199 |
| 40329 | 42526 |
| 43321 | 39527 |
| 41386 | 42536 |
| 41474 | 43611 |

The native probe report records every cap's circular fit and each blend's
failed revolved-wall fit:
`artifacts/cleanup-diagnosis-bf14a9072ffc45acae65a2c692e2f95e/report.txt`.

## Recognition and deletion guard

A composite region now requires at least one real inward cylindrical/conical
bore wall. Analytic faces use the existing inner-wall check; imported surfaces
must pass the existing multi-station cylinder/cone profile fit and void check.
The wall axis must match the candidate bore axis. A circular cap alone is
insufficient. This is conservative for very shallow rounded recesses lacking
any distinct bore wall.

Deletion repeats the requirement on current faces for every individual group
before flattening a batch. This prevents one valid hole in a batch from
authorizing deletion of a neighboring rounded end, and protects against stale
or externally supplied previews. Rejection creates no committed feature;
existing independent rollback, partial-success and Stop behavior remain.

## Regression scope

`tests/data/housing-hole-coverage.txt` requires all 29 exact valid groups /
64 faces, including the earlier six missed holes, and forbids all ten rib-end
faces. `tests/data/housing-rib-ends.txt` identifies the protected faces.

The Apply regression accepts `-ProtectedFacesPath`. It requires each protected
face and its edge tags to remain, and compares nine points on every protected
edge before/after (maximum coordinate displacement 0.000001 part units).
This checks the rounded boundaries directly, beyond body validity or whether
a sampled empty point becomes solid.

Required runtime cases:

1. Scan the unchanged housing: 29 groups, exact expected sets and all negatives.
2. Supply the old 13 added groups to Apply: eight real bore groups may fill;
   five rounded ends must be rejected and all ten protected faces/curves kept.
3. Supply only the five rounded ends: zero removals, failure callback and all
   protected faces/curves unchanged.
4. Preserve the six positive hole cases, body consistency and outer undo.

The earlier 34-group/13-added-group results are historical and are not current
selection expectations. They demonstrated native healing and callback behavior,
not the intended shape after removing a rib end.

Verified runtime reports:

- Recognition: `artifacts/cleanup-diagnosis-ff2eb7e931c943919aa61f241473ff40/report.txt`;
  29 exact groups / 64 faces, six positive arrow cases retained, five bad groups
  excluded, outer undo and original file hash preserved.
- Mixed old preview: `artifacts/fill-holes-apply-7442775fef194147851cfce2a6d64fd3/report.txt`;
  eight groups / 33 faces filled, five rejected, all ten protected faces and
  sampled boundary curves unchanged, consistency and undo passed.
- Bad-only preview: `artifacts/fill-holes-apply-c6d74d338005408fa3b128a308ac30a0/report.txt`;
  zero faces removed, callback failure returned, protected curves unchanged.
- Original nine baseline hole groups on the 958-face fixture remain present:
  `artifacts/cleanup-diagnosis-378ab822d95f4fbba75447390b20944e/report.txt`.
  Three other rounded, wall-less recess groups are now excluded there too.
- The conical fixture retains its exact complete group:
  `artifacts/cleanup-diagnosis-45928ddb0b384baebbb501388cf5bc8a/report.txt`.
