# NX Connected Blend Faces investigation

## Native command and API

The selection intent used by NX's **Delete Face > Connected Blend Faces** is available as:

```csharp
FaceConnectedBlendRule rule = part.ScRuleFactory.CreateRuleFaceConnectedBlend(seedFace);
collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
Face[] chain = collector.GetObjects().OfType<Face>().ToArray();
```

Source: installed NX 2312 `NXBIN/managed/NXOpen.xml`, entries for `NXOpen.ScRuleFactory.CreateRuleFaceConnectedBlend`. The one-argument overload exists since NX 5.0; overloads additionally offer `includeBlendLike` and `includeUnlabeledBlend` recognition. A connected-blend rule selects a geometric chain; it does not guarantee the selected faces can be healed.

## What the baseline showed

All experiments opened `artifacts/blend-bench/model_baseline.prt` in a separate NX process without saving. Individual chain probes were undone between deletions.

- Original radius filter: 524 faces in (0, 3] part units.
- Ordinary topological adjacency joined faces into 36 components, including a 299-face component. Splitting those components at arbitrary 32-face boundaries did not follow the geometric blend chains.
- Native default recognition: 180 distinct nonempty rule results in about 3 seconds. Sizes included 46, 38, 16, 14, 8, and several 7-face chains. Results can overlap; merging overlapping results would undo the benefit.
- Default rule added no faces outside the original radius-filtered selection on this fixture. Some selected seed faces were not in the returned chain; those must be handled separately, not forcibly appended to the chain.
- Native 46-face and 38-face chains both healed successfully in one operation (875 ms and 699 ms in the probe). Of the 12 largest tested chains, 11 healed; the 16-face chain failed. Several crossing 7-face chains healed in 110–236 ms.
- Enabling both broader recognition flags produced 143 distinct regions and took about 31 seconds. It added no extra candidate faces here. Production uses the default overload; broader recognition was not justified by these results.

## Integration

`RemoveBlendsDialog.BuildConnectedGroups` now queries native connected-blend rules, deduplicates identical sets, preserves overlapping chains separately and intersects each set with the user-reviewed selection. Uncovered selected faces receive single-face fallback groups. It never silently selects radius-excluded or deselected faces.

Deletion first attempts the intact native chains, largest first, before expensive fallback retries. The per-attempt limit is now 128 faces, so the verified 38/46-face chains stay intact. The existing 256-attempt / 120-second budget and rollback checks remain. All actual commits use explicit reviewed face sets, rather than a live rule that could expand after topology changes.

The regression harness checks exact selection coverage, verifies that removing a face from the reviewed set does not reintroduce it during chain recognition, measures actual deleted tags on current bodies, verifies solid preservation, and checks full undo restoration. The real-part regression threshold is raised to 400 removals for this fixture.

## Final production regression

On NX 2312, the final implementation produced 246 deduplicated chain/fallback groups, largest chain 46 faces. It removed **410 of 524 selected faces** in **56666 ms**, using 256 attempts. Actual and reported counts matched; solid body count/type and full undo restoration passed. The selection-boundary check passed. No part was saved.

This compares with 77 removals / 122728 ms for the preceding adjacency-based implementation on the same baseline. The final run reached the attempt budget, so the remaining 114 faces are not all proven unhealable. Deletion order and the kernel budget can affect how much work finishes in one pass. The earlier exploratory integration reported 424 removals; 410 is the measured final-build result.

## Superseded deletion policy

The 410-face result above describes the earlier unrestricted deletion policy. It was superseded by atomic chain rollback and local shape checks after reports of partial chains and pointed extensions. See BLEND_SHAPE_SAFETY.md for the current 195-face quality regression and retained-geometry policy. Native connected-blend recognition remains in use.
