# Remove Blends: partial chains and remote extensions

This is an earlier experiment. The later region-safety experiment was also
reverted at the user's request. The active policy is [NATIVE_BLEND_RETRIES.md](NATIVE_BLEND_RETRIES.md).

## Investigation

The preceding implementation optimized the number of disappearing face tags, but did not establish that the healed shape was acceptable:

- Failed native chains were split into arbitrary halves/single faces. Successful fragments remained committed when the remainder failed or the budget expired.
- Overlapping native chains were separate commits without a shared rollback boundary. Healing one chain can also change faces belonging to another chain, so even a locally complete commit can leave a different chain partly removed.
- `DeletePartialBlend` was enabled in the native deletion fallback.
- Generic Delete Face with Heal can extend support surfaces to an intersection. A result may remain a closed solid while acquiring a remote pointed extension. The old postcondition only required at least one selected tag to disappear; it did not check complete removal or the extent of the resulting shape.

These mechanisms match the partial transitions and pointed structure in the screenshots. The screenshots alone do not identify the exact kernel operation or face tags. On the existing baseline part, the new locality check rejected multiple healed operations that created vertices outside the local blend region. This is direct evidence of unacceptable extension under the new conservative rule, not proof that each rejected operation corresponds to the specific pictured tip.

## Changes

- Preserve native chains as healing operations. Overlapping chains share an atomic transaction; all selected faces in that dependent component must disappear or the whole component is restored.
- Check every original native chain after each component: all its original selected faces must remain, or all must be gone. This catches collateral changes to other chains, not only the explicitly deleted input set.
- Remove arbitrary chain slicing and single-face salvage of failed chains. Originally isolated single-face components can still be deleted.
- Disable `DeletePartialBlend`. A committed attempt must remove all explicitly selected faces.
- Snapshot affected solid bodies, their existing edge vertices and each selected face's bounding box. Permit new/moved vertices only inside the union of local boxes expanded by twice that face's recognized radius. Unchanged vertices outside this region are allowed. Otherwise undo the attempt.
- Check locality again against the component's original shape before keeping the component, so cumulative changes are checked too.
- Stop processing if an attempt rollback fails, instead of silently continuing on uncertain geometry.
- Give all components their normal healed attempt before expensive native retries. Budget exhaustion rolls back an unfinished component.

The locality check is intentionally conservative. It is a vertex/box check, not a full surface-distance, curvature or manufacturing-quality proof. It can reject legitimate intersections beyond twice the blend radius; it does not prove that every possible internal spline distortion is absent. An unchanged/retained complex fillet is preferable to accepting a partially deleted chain or a suspicious remote extension.

## Verification

`RunNxBlendCollectorRegression.cs` checks normal native fillet removal and undo, and verifies that a tall replacement solid is rejected by the locality guard despite still being a solid body. Passed against NX 2312.

`NxRemoveBlendsBench.cs` now asserts whole-chain atomicity in addition to selection boundaries, actual deletion counts, solid preservation and full undo restoration. Deletion count is no longer the sole acceptance criterion; the previous 400-face performance threshold is inappropriate for this conservative mode.

Final baseline result: **524 candidates, 195 removed, 195 reported, 256 attempts, 98053 ms, budget reached**. `WHOLE_CHAIN_ATOMICITY=PASS`; selection boundaries, solid bodies, count accuracy and full undo restoration passed. No input part was saved. The remaining 329 were retained or not exhausted; do not treat them all as impossible to heal.

For a baseline quality regression use `./scripts/test-nx-remove-blends.ps1 -PartPath ./artifacts/blend-bench/model_baseline.prt -MinimumRemoved 150`. The stronger atomicity checks are mandatory regardless of the numerical threshold. The old unrestricted native-chain benchmark (410 removals) is historical and does not establish acceptable shape quality.

Existing geometry already damaged by an older run is not repaired automatically: undo the old Remove Blends operation or reopen an undamaged copy before using this version. Screenshot locations were not individually matched to saved face tags; the integration tests verify the defined topology and locality conditions, not a visual guarantee for every model surface.

## Superseded by user-requested native behavior

The user subsequently requested weak seed-radius control and removal of over-conservative plugin restrictions. Current behavior is in WEAK_RADIUS_NATIVE_DELETE.md: each live native connected-face operation is independent; overlapping-chain rollback and the two-radius spatial veto described above are no longer active. This document records the earlier experiment, not the current implementation.
