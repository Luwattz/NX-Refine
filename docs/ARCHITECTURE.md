# Architecture

NX Refine deliberately separates geometry inspection from geometry mutation.

1. `GeometryAnalyzer` combines NX Examine Geometry checks with configurable heuristics for short edges, small faces, blend radii, and cylindrical hole candidates.
2. `CleanupEngine` groups repair candidates by owning body and executes each operation under one visible NX undo mark.
3. Ribbon MenuScript files invoke the same managed DLL with a command argument.
4. User thresholds are stored in `%APPDATA%\NXRefine\settings.ini`; Remove Markings' maximum feature height is persisted separately in millimetres, and Fill Holes' maximum radius is persisted in current part units.
5. Clear Cavities classifies NX exterior faces with UFUN ray analysis, groups the remaining faces by edge connectivity, and heals only user-retained closed internal shells through a native Delete Face builder.
6. Repair Unattached Faces uses bounding-box broad-phase filtering, positive UFUN minimum distances, opposing local normals, non-collinear patch samples, and empty-space containment checks. The sweep axis minimizes interval overlaps; planar normals are cached within a scan and prefilter planar pairs. Patch validation exits as soon as a qualifying non-collinear triple is found. Unchanged native commits reuse the scan, including empty results; geometry edits reset it. Scan duration and kernel query counts are logged. Only smaller problem-side faces participate in preview grouping; a shared carrier does not join independent gaps. Retained pairs dispatch to native Sew (separate bodies) or Delete Face/Heal (same body). Sampling is conservative, not exhaustive, and intentional clearances still require user review.

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
