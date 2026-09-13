# Repair Unattached Faces regression checklist

These checks require Siemens NX 2312 and should be run on disposable copies of the sample parts.

## Dialog and selection

- [ ] Geometry Cleanup contains **Repair Unattached Faces** in the Repair group and opens the native Block Styler dialog.
- [ ] Target entities accepts one or more solid bodies and ignores sheet bodies or assembly occurrences.
- [ ] The maximum gap field starts from its saved value (default `0.01`) and accepts decimal input in current part units.
- [ ] Typing a value such as `0.2` does not start a scan after the first `0`; Enter, leaving the field, or an action button commits it once.
- [ ] The candidate collector is populated only after a body is selected; candidate faces are highlighted while the selected body is not highlighted as a whole.
- [ ] Cancel, Escape, and closing the dialog clear all candidate highlights and leave geometry unchanged.

## Detection

- [ ] Repeating a native commit at the same gap, including after a scan with no candidates, does not repeat the scan. Changing bodies still rebuilds.
- [ ] Compare candidates before/after optimization on the same part and tolerance. The NX log reports total, preparation and search milliseconds, box tests, close pairs, distance/containment queries, planar rejections and projection cache hits; actual speed depends on topology.

- [ ] A fully joined face pair sharing a topological edge is not reported. Opposing planar faces with a positive local open patch can still qualify even if an edge is joined.
- [ ] Faces touching only at a vertex, zero-distance contacts, same-facing surfaces, and back-to-back thin walls are excluded.
- [ ] A known small separation between a rib bottom and its base face is reported with only the smaller problem-side face highlighted.
- [ ] A known small local face-to-face gap is reported when its positive distance is within tolerance and a two-dimensional patch of opposing faces borders sampled empty space.
- [ ] Deliberate clearances greater than the tolerance are not reported.
- [ ] Multiple independent gaps sharing the same large support face remain separate groups. Only connected problem-side faces are grouped.
- [ ] A smaller tolerance rebuilds the candidate set and clears stale highlights from the previous value.
- [ ] Back-to-back parallel walls and coplanar pairs are rejected before expensive distance/grid queries; tilted partially open planar gaps still qualify.
- [ ] A repeated smaller source face paired with several carriers reports projection cache hits without changing the accepted groups. Tolerance-only changes reuse geometry and minimum measurements, while recalculating the candidate set. Returning to a larger tolerance restores the original candidates.
- [ ] Body selection changes, part modifications (including undo), work-part changes and repairs invalidate cached face data and measurements. The next scan reports `geometry reused=False`. Stale candidates cannot enable Apply/OK; rescanning requires review before repair.
- [ ] A planar face containing a through-hole never treats points in that hole or on a trim boundary as interior analytic projections. Interior projections match NX minimum distances in millimeter and inch parts; curved or uncertain faces retain the NX solver.
- [ ] A minimum distance definitely above the limit skips the 25-point fallback. Zero minima, uncertain thresholds and failed local patches still receive partial-gap checks when otherwise eligible.
- [ ] Sparse selections read point/normal geometry only for faces surviving the spatial filter. Tolerance-only rescans report fewer geometry reads and positive measurement-cache hits on repeated pairs.
- [ ] Lazy native adjacency produces the same groups as edge traversal, including sheet-like imported topology, self-adjacent seams and vertex-only contacts. Cross-body pairs still skip same-body adjacency checks; grouping loads adjacency for every problem-side face.
- [ ] Repeated exact point queries show projection, curved-normal and solid-containment cache hits. Distinct points on opposite sides of a tiny gap stay distinct; geometry/selection changes clear all point caches.
- [ ] Tilted planar target samples outside the maximum plane distance skip projection, while all in-range/boundary samples retain the original checks. Candidate results match the previous full sampling pass.
- [ ] A large sparse part no longer reaches the pair limit merely because many face boxes overlap on a single coordinate axis.

## Review and commit

- [ ] Deselecting any face in a connected candidate group removes the complete group from the preview and pending repair.
- [ ] **Apply** repairs only retained groups, leaves the dialog open, and refreshes the preview.
- [ ] **OK** performs the same repair and closes the dialog.
- [ ] Same-body gaps use Delete Face with Heal on the smaller candidate face; separate solid bodies use native solid Sew.
- [ ] A failed repair pass returns to the single NX undo mark created for that pass.

## Automated numerical and spatial checks

Run `powershell -ExecutionPolicy Bypass -File scripts/test-gap-criteria.ps1` without NX. This compiles the production criteria, point cache and spatial index, compares ordered results with independent exhaustive box-distance checks (random, flat, coincident, boundary and large-carrier cases), and checks that planar bounds preserve admissible gap witnesses in millimeter and inch units. Analytic projection is checked against known feet/distances on 3,000 independently rotated planes, including non-unit and reversed normals. Minimum-distance exclusion tests ensure uncertainty overlapping a valid gap never suppresses partial verification. Cache checks cover exact-coordinate/entity isolation, input-array mutation, capacity, invalidation and changed tolerance decisions. Sample-plane bounds preserve 2,000 rotated in-range witnesses. Current result: 77,228 checks passed.

The synthetic sparse benchmark contains 32,768 boxes and includes index construction in its timing. On the development machine, the previous sweep required 16,760,832 box checks versus 839,361 for the index (95% fewer); one run took 78 ms versus 49 ms. These measure broad-phase work only, not NX part scan or repair time. The benchmark asserts operation reduction, not a machine-dependent time threshold. Actual NX candidate equivalence and end-to-end timing require the manual part checks above.

## Optional NX kernel projection test

Run `powershell -ExecutionPolicy Bypass -File scripts/test-nx-gap-projection.ps1` in an environment supporting NX batch journals. `-CompileOnly` validates compilation without starting NX. The test creates unsaved synthetic square frames with through-holes in a separate process, in both unit systems. It checks native adjacency against edge traversal, checks interior/hole/boundary/exterior classifications and compares analytic projections with the original NX solver; it also times 500 interior samples per unit system. It does not open or modify user part files.

The test compiled against NX 2312 locally, but execution could not be completed: the NX batch runner exited with `General Fault Exception` / `Fatal error detected` before producing test results. No NX kernel or real-part speedup is claimed from this run. Callback invalidation and complete candidate equivalence remain NX integration checks.
