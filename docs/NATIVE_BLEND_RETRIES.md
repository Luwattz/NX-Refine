# Native recognition retries after zero removals

This is the restored active policy following the user's request to revert the
overly conservative region-safety experiment. Whole-region rollback, local
shape veto and per-region budget allocation have been removed. Original seed
retention, all four native recognition modes and weak radius filtering remain.

## Reproduced failure

On the isolated housing baseline, the previous DLL removed 438 of 524 preview
faces in the first invocation. The second invocation had 76 preview faces and
removed zero. This is a reproduction of the failure pattern, not the user's
exact saved 81-face intermediate state.

On that same unchanged remaining geometry, independent live-rule Delete Face
probes gave:

| includeBlendLike | includeUnlabeledBlend | Result |
| --- | --- | --- |
| true | true | Six largest tested chains failed to heal |
| false | false | A four-face chain and a two-face chain healed |
| true | false | An eight-face chain healed |
| false | true | Six largest tested chains failed to heal |

Each probe used a live `FaceConnectedBlendRule` in `DeleteFaceBuilder.FaceCollector`,
`Type=Face`, and `Heal=true`; each probe was undone. No input part was saved.
The two flags change the recognized face set. Enabling both is not a guarantee
of a more useful or healable connected chain. This explains a demonstrated
case where the fixed expanded mode stops but another native mode succeeds.

## Implementation

- Retain the radius-filtered source seeds independently of the preview result.
  A native rule's source seed is not guaranteed to appear in its returned faces.
- Start with expanded recognition (both flags true). If a pass makes no
  progress, try false/false, true/false, then false/true.
- For each mode, evaluate complete native rules from the current surviving
  seed pool. Deduplicate equal results, and re-evaluate the live rule in the
  actual builder before committing. Do not clip by radius, union overlapping
  chains, or fall back to fixed/single-face deletion lists.
- Skip an already attempted equal face set while topology is unchanged.
  Clear that failure cache after a successful deletion and recognize again
  on the next pass. Preserve the existing operation time/attempt budget.
- All modes honor manually deselected preview faces and retain the existing
  complete-chain removal, solid-body and body-consistency checks with rollback.
- Failure logs include seed, recognition mode, selected face count and NX error
  code. Mode bits are 1 = blend-like, 2 = unlabeled.

## Validation

All four modes passed the synthetic NX journal: native deletion, radius ignored
after seed recognition, manual exclusion, solid preservation and undo.

Production repeated-run results on the same in-memory baseline:

| Invocation | Preview faces | Actual/reported removals | Attempts | Time (ms) | Budget reached |
| --- | ---: | ---: | ---: | ---: | --- |
| 1 | 524 | 437 | 178 | 120350 | yes |
| 2 | 81 | 28 | 256 | 112625 | yes |
| 3 | 52 | 0 | 49 | 20350 | no |

NX logs confirm successful fallback modes 0 and 1, including an eight-face
mode-1 chain, and further successful mode-3 deletions after those changes.
Reported counts matched current topology; solid bodies and full original undo
restoration passed. No input part was saved. The first-round cutoff is time
dependent, so the old and new first-pass states are not identical. The 52
remaining preview faces were not removed by the tested native combinations;
this change does not establish that every screenshot fillet has been removed.

`scripts/test-nx-remove-blends.ps1 -RepeatCount 3` exercises repeated invocations
without undoing between rounds. It checks actual versus reported deletion
counts, current topology resolution and solid preservation per round, then
restores the entire original part with one outer undo mark.
For this housing fixture, `-MinimumSecondRoundRemoved 1` additionally rejects a
regression to zero removals on the second invocation.

The ignored evidence files are under `artifacts/blend-bench/`:
`terminal-old.log`, `native-mode-multiround.log`, `native-mode-synthetic.log`.
