# NX Examine Geometry and unattached-face search

Investigation date: 2026-09-12. Primary evidence is the installed Siemens NX 2312 SDK and the current repository. No real-part timing was obtained in this investigation and no repair behavior was changed.

## What the screenshot establishes

The dialog reports 958 selected objects. Object and face checks have results, while every body check and both edge checks show no result. This is consistent with a face selection, rather than 958 fully examined bodies. The screenshot alone does not identify each selected object's type or the raw result status, so this remains an inference.

The native API explicitly distinguishes an unselected check, a check with no relevant selected objects, an error, and a check skipped because a prerequisite failed. Selecting all checkboxes does not prove that all checks were performed. `ExamineGeometry.GetResults()` documents these states in `NXBIN/managed/NXOpen.xml` around line 596738 and `UGOPEN/NXOpen/GeometricAnalysis_ExamineGeometry.hxx` around line 170.

The displayed distance of 0.1000 is an Examine Geometry criterion. It is not evidence that NX searched every non-adjacent pair for a positive face-to-face gap up to 0.1000.

## Detection scope and available interfaces

| Screenshot check | Native interface | Relevance to our repair search |
| --- | --- | --- |
| Tiny | `ExamineGeometry.Check.ObjectTiny`, `UF_MODL_ask_tiny_geometry` | Flags small geometry; does not establish an opposing face across empty space. |
| Misaligned | `ObjectMisaligned`, `UF_MODL_ask_misalign_geometry` | Checks slight directional misalignment against a reference matrix with an angular tolerance. Does not measure a gap between two faces. |
| Body structures / consistency | `BodyDataStructures`, `BodyConsistency`, corresponding UFUN checks | Checks an existing body's topology/geometry validity. Two separate valid solids can still have a small clearance. |
| Face intersections | `BodyFaceIntersections`, `UF_MODL_ask_face_face_intersect` | Detects intersections within a body, rather than positive separation. SDK prerequisites include body structure/consistency checks. |
| Sheet boundaries | `BodySheetBoundaries`, `UF_MODL_ask_body_boundaries` | Identifies boundary edges of sheet bodies. Does not identify all gaps bounded by solid faces. |
| Face smoothness / self-intersection | `FaceSmoothness`, `FaceSelfIntersection` | Tests properties of an individual face. |
| Spikes / cuts | `FaceSpikesCuts`, `UF_MODL_ask_face_spikes` | The documented UFUN routine examines adjoining boundary edges with a small included angle and samples the shorter edge against the longer one. This is a local boundary test. |
| Edge smoothness / tolerance | `EdgeSmoothness`, `EdgeTolerances` | Tests an edge and its existing topology/tolerance, rather than searching for unconnected nearby faces. |

The corresponding official SDK definitions are in `UGOPEN/uf_modl.h`, around lines 4028–4289. The high-level `ExamineGeometry.Check` enumeration contains no positive face-gap or unattached-face search option.

## Why the runtime comparison can differ

The most defensible explanation is different work, not a confirmed hidden acceleration technology:

1. The screenshot suggests that body and edge checks did not contribute work. Selecting bodies in our repair dialog expands them into faces and edges, prepares adjacency, and searches nearby face pairs.
2. Many selected native checks concern individual objects or already-known adjacent edges. A set of 958 faces has 458,403 possible unordered pairs before spatial rejection. Our index rejects many, but surviving pairs still require geometric queries.
3. The current repair code asks for a face-pair minimum, then verifies a two-dimensional patch with opposing normals and empty-space samples. Its regular search has up to 18 sample positions; a qualifying same-body fallback has up to 25. Projection caches and analytic planar projections reduce calls, but do not remove those geometric obligations.
4. `ExamineGeometry.Examine()` submits the selected native checks together and provides failed-object arrays. Our narrow-phase search orchestrates repeated native queries from managed code. This is a plausible further source of overhead; its contribution has not been profiled on the user's part.

The public SDK does not establish that this command uses a particular internal spatial index, multithreading, GPU processing, or a specific complexity bound. No such claims should be made from the observed speed alone. Native body intersection and consistency checks can also be expensive on difficult geometry.

## Existing integration and recommended direction

`src/NXRefine/Analysis/GeometryAnalyzer.cs:29` already creates the official `ExamineGeometry` object, selects all checks, calls `Examine()`, and retrieves failed objects by check. That Analyze operation subsequently adds its own edge, blend, cylinder and face-area queries, so its total runtime is not a measurement of the native Examine call alone.

For defects matching the screenshot's Tiny or Spikes/Cuts results, use the native failed-object lists as the detection source and measure just the required checks. Those defects still need an appropriate repair policy; native diagnosis alone does not authorize deleting every reported face.

For the original positive-gap requirement, native failed-object lists cannot safely be the exclusive candidate filter: valid faces on valid solids may bound a small gap while passing all native geometry checks. They could order a progressive search, but an exhaustive mode must still search the remaining relevant faces. A fast mode limited to native failures would have different detection coverage and must be described as such.

Before claiming equivalent speed or replacing the detector, compare the same part, object selection types and intended defect. Record native per-check states and failed tags, then compare those tags with the repair gap groups and separately time preparation, distance queries, patch checks and any actual repair. The most useful next experiment is whether the user's unwanted faces are exactly the native Tiny/Spikes-Cuts failures, or additional valid faces separated by a small open gap.

## Follow-up implementation for the confirmed positive-gap requirement

The user confirmed that the desired defects are not the Tiny/Spikes-Cuts failures. The implementation therefore continues to search the full spatial candidate set; no native diagnostic failure list is used as an exclusion filter.

The following improvements reuse native topology and avoid repeated geometry work:

- `UF_MODL_ask_adjac_faces` supplies immediate shared-edge neighbors directly (SDK `uf_modl.h:2778` explicitly excludes vertex-only adjacency). Adjacency is loaded only for surviving same-body comparisons or actual problem faces needed for grouping. A face-edge traversal remains the fallback if the native query throws. This removes unconditional traversal of every body edge during preparation.
- Exact-coordinate caches retain successful point projections, curved-face normals and solid containment statuses in the existing geometry snapshot. They have independent 50,000-entry limits and do not quantize coordinates. The current maximum gap is still checked on every scan; geometry/selection invalidation clears these caches with the existing minimum-distance cache.
- Before projecting a sample to a planar target, distance to its infinite supporting plane provides a conservative lower bound. Samples definitely beyond the gap cannot qualify anywhere on the trimmed target. Unknown planes and uncertain thresholds continue to the original solver.

Native UV containment batching was also investigated. The SDK supports it, but it requires correct UV coordinates and containment-resource lifetimes; those introduce additional mapping and lifecycle requirements. This change retains the already integrated trimmed-face containment path instead of introducing an unverified UV conversion.

Validation: 77,228 numerical, cache and spatial checks passed. A synthetic repeat-query workload required 200 evaluations for 600 requests across two geometry revisions and three tolerance settings, with unchanged decisions. This is a cache behavior check, not an NX speed measurement. Full native candidate equivalence and timing remain to be checked in an interactive NX session.
