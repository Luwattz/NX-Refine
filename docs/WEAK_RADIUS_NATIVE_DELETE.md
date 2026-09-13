# Native connected-face deletion and weak radius filtering

## Current behavior

The interactive Remove Blends command now applies radius limits only to initial seed recognition. Preview construction evaluates `CreateRuleFaceConnectedBlend(seed, true, true, null)` (including blend-like and unlabeled faces) and displays the full union of its results on the selected bodies. It does not intersect those results with the initial radius-filtered set. Faces outside the seed range can therefore be selected and deleted.

Deletion creates `DeleteFaceBuilder` with `Type=Face`, `Heal=true` and a live `FaceConnectedBlendRule` in `FaceCollector`, matching the selection intent of manual **Delete Face > Connected Blend Faces**. The real builder receives the native rule, not a `FaceDumbRule`. There is no additional maximum radius at commit time, singleton salvage or alternative deletion mode. NX-unrecognized seed faces are not forcibly added to a rule result.

Rules are refreshed from current topology after successful deletion. Each native
operation commits or rolls back independently. The conservative region rollback,
local shape veto and per-region budget were reverted at the user's request.
Radius filtering remains weak. The global budget remains 256 attempts / 120
seconds between operations. See [native retries](NATIVE_BLEND_RETRIES.md).

Manual deselection is distinct from weak radius filtering: if a current rule includes a manually excluded face, that whole operation is skipped. The rule is not truncated. Scope remains the selected body. Complete removal of the current rule result, solid preservation and NX `AskBodyConsistency` error-count checks remain. A valid NX solid is not a comprehensive visual-quality proof; inspect results just as for native manual deletion.

## Earlier verification before expanded recognition

- Release compilation against NX 2312 passed with existing targeting-pack/obsolete API warnings.
- Synthetic journal: live rule removes the 1 mm rounded edge even when the stored seed maximum is reduced to 0.01 after recognition; manually excluding the selected face prevents deletion; solid and undo checks passed. No part saved.
- Baseline: 524 radius-filtered seeds expanded to 458 distinct faces from 180 native results. Fewer output faces are possible because some initially recognized seeds are not accepted by the native rule; they are no longer forced into singleton deletion.
- Production run: **376 of 458 preview faces removed**, 243 commit attempts, 120113 ms, budget reached. Actual and reported counts, solid preservation and full undo restoration passed. Single-seed expansion beyond the input face subset also passed.
- This baseline's broad (0, 3] seed range did not demonstrate additional faces above 3 mm. The independent post-recognition radius-gate regression establishes that the deletion path does not impose that limit again. Out-of-range expansion depends on what the native connected rule actually selects.
- Tests used unsaved isolated NX sessions. They did not reproduce the user's exact 283-face intermediate state, so this result is not a claim that all 283 faces will disappear in one run.

## Reproduce

Build Release, then run `scripts/test-nx-remove-blends.ps1 -PartPath artifacts/blend-bench/model_baseline.prt -MinimumRemoved 150`. Run `tests/RunNxBlendCollectorRegression.cs` using NX `run_journal.exe`, passing the absolute Release DLL path.

Earlier chain-atomicity/spatial-veto tests described in BLEND_SHAPE_SAFETY.md are historical and superseded by this user-requested native-command behavior. Current dialog labels explicitly identify the radius fields as seed limits.

The current recognition options and loop-specific results are in EXPANDED_BLEND_RECOGNITION.md. Preview and commit share the expanded rule factory.

