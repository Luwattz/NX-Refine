# Architecture

NX Refine deliberately separates geometry inspection from geometry mutation.

1. `GeometryAnalyzer` combines NX Examine Geometry checks with configurable heuristics for short edges, small faces, blend radii, and cylindrical hole candidates.
2. `CleanupEngine` groups repair candidates by owning body and executes each operation under one visible NX undo mark.
3. Ribbon MenuScript files invoke the same managed DLL with a command argument.
4. User thresholds are stored in `%APPDATA%\NXRefine\settings.ini`; Remove Markings' maximum feature height is persisted separately in millimetres, and Fill Holes' maximum radius is persisted in current part units.
5. Clear Cavities classifies NX exterior faces with UFUN ray analysis, groups the remaining faces by edge connectivity, and heals only user-retained closed internal shells through a native Delete Face builder.
6. Repair Unattached Faces uses a scan-local bounding volume hierarchy to enumerate face boxes within the gap tolerance in all three dimensions. Query results preserve the previous sweep ordering; the safety limit counts spatially close pairs instead of one-axis overlaps. Signed planar bounds reject boxes entirely behind an outward plane or beyond the gap limit before kernel queries. Face points and normals load on demand. Planar point queries use analytic projection only when UFUN classifies the projection as strictly inside the trimmed face; boundary, hole, exterior, uncertain and unsupported queries fall back to the original minimum-distance solver. Positive UFUN distances, opposing local normals, non-collinear patch samples, and empty-space containment still validate retained gaps. Same-body opposing planar faces also receive a partial-gap grid check, including adjacent faces and zero-minimum pairs, unless a valid minimum-distance lower bound exceeds the search limit. A dialog-local geometry snapshot retains face data, adjacency, partial-source projections and up to 50,000 face-pair minimum measurements across tolerance edits. Pair measurements retain point orientation when retrieved in reverse; patch acceptance and empty-space checks always rerun for the new tolerance. Part modification, close and work-part events invalidate the snapshot; selection changes, errors and repair reset it. Callbacks mark revisions only, and scans interrupted by modification cannot enable repair. Patch validation exits as soon as a qualifying non-collinear triple is found. Unchanged native commits reuse the scan, including empty results. Logs report preparation/search times, kernel queries, analytic projection hits, geometry/measurement reuse and rejected or skipped pairs. Only smaller problem-side faces participate in preview grouping; a shared carrier does not join independent gaps. Retained pairs dispatch to native Sew (separate bodies) or Delete Face/Heal (same body). Sampling is conservative, not exhaustive, and intentional clearances still require user review.

Gap-query acceleration also uses on-demand `AskAdjacFaces` calls, with edge traversal as the failure fallback. Successful point projections, curved-face normals and solid point-containment statuses are cached by exact object tag and coordinates (up to 50,000 entries per cache) within the same geometry snapshot. No coordinate quantization is used; classifications may be reused across gap edits while gap acceptance is reevaluated. Each cache is cleared when rebuilding an invalidated snapshot or resetting before repair. Sample-to-plane distance bounds skip only points whose distance to the entire supporting plane already exceeds the gap. Logs count adjacency reads, point-query cache hits and skipped samples.

## Safety model

- Analysis is non-destructive and highlights findings.
- Repair candidates are highlighted before confirmation.
- A failed multi-body repair rolls back to the operation's undo mark.
- Complex missing-face reconstruction is preview-only in v0.1 because choosing a valid replacement surface requires topology- and continuity-aware planning.

## Roadmap

- Concavity-based pocket segmentation for open cavities.
- Connected-component grouping of sliver faces and small structures.
- N-sided, planar, and through-curve-mesh patch strategies.
- Dumb-solid engraving recognition using shallow concave face regions.
- JSON/CSV audit reports with before/after validation.
- Recorded regression parts and automated NX batch tests.
