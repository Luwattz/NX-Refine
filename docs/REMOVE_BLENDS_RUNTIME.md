# Remove Blends deletion runtime

The long-running `housing.prt` session in the user screenshot had completed
1,009 native deletion attempts and removed 1,687 of 4,361 selected faces in
45 minutes 22 seconds. The corresponding NX log contained 383 successful
commits, 621 kernel failures and five body-consistency rollbacks. Recognition
was not the dominant cost in that run. A live native fallback could also grow
a failed two-face rule into 15 faces and spend several minutes inside NX.

## Expensive validation

An isolated native probe on the unchanged 6,120-face housing measured 26 ms
for face enumeration and 3,302 ms for one `AskBodyConsistency`. In a successful
44-face / 3-face pair, separate operations spent 5,968 ms checking the same
body. The paired check spent 2,955 ms. Blindly combining native selection
rules was rejected: five of six tested pairs failed, a failed member poisoned
otherwise healable members, and some combined commits were slower.

Evidence:

- `artifacts/blend-batch-probe-9de4617b68604d11a8c487b2fdfdb9a6/report.txt`
- `artifacts/blend-batch-probe-1b43ffd00768452c9c20b30f01938f39/report.txt`
- `scripts/test-nx-blend-batch.ps1`

Narrower fallback preflights were also rejected. In the guarded first-source
probe, all four tested narrower alternatives to actually failing mode-3
operations failed too. Querying extra modes added work without a demonstrated
recovery. Evidence:
`artifacts/blend-narrow-effea525959e426ca52b550f39bdd231/report.txt`.

## Transactional consistency checks

Each complete live native rule still gets its own Delete Face builder and
individual undo mark. No rules or fixed face subsets are combined. Every
successful kernel call must remove its entire selected chain and leave a solid.

A second, outer undo mark groups at most four actual kernel commit attempts
(including failed calls), or 16 dequeued jobs. All affected bodies receive a
whole-body consistency check at the end of the group. Only validated changes
are counted in the displayed deletion progress. Unchanged/failing-only groups
need no additional body check.

If validation fails, undo the group and restore the complete scheduler state:
both job queues, source pool, attempted face-set cache, consistency baseline,
and pass-progress flag. Replay that group's jobs with a separate consistency
check after each successful operation. This isolates bad heals while retaining
the good operations. The strict replay cursor survives Stop/Apply.

On Stop, budget expiry or error, pending changes are validated before returning.
An invalid pending group is rolled back and left queued; no replay runs after
the stop request. Stop still waits for an active NX kernel call and the final
body validation. These native calls cannot be interrupted safely from the
progress-window thread.

Additional changes suppress viewport updates during interactive cleanup and
regenerate the display once after restoring its original display state. The
independent progress window remains active. A successful deletion reuses its
already resolved face map instead of enumerating the solid twice. Logs report
recognition, commit, consistency, undo and face-map time separately, including
the consistency-check count.

## Regression

`scripts/test-nx-blend-collector.ps1` covers all four live native recognition
modes, weak radius policy, manual exclusions, original source retention,
continuous interactive processing, Stop/Apply, solid preservation and undo.

`scripts/test-nx-blend-grouped.ps1 -PluginPath <candidate>` exercises real
native commits on an unsaved housing copy. It forces group validation failure,
checks scheduler restoration and strict replay, and covers Stop with both valid
and invalid pending edits. It also compares strict and grouped throughput from
the same initial geometry, checking final body consistency and restoring all
original face tags with undo. The script verifies the original file hash.

Batch measurements do not include interactive viewport-regeneration savings.
Short timed runs measure throughput on a fixture, not total cleanup time or
the final removal ratio. Some native chains remain unhealable.

## Housing comparison

NX 2512, same unsaved starting housing geometry, first 64 radius-qualified
sources in the existing sorted order, each run requesting Stop at 90 seconds:

| Measurement | Individual checks | Grouped checks |
| --- | ---: | ---: |
| Wall time including stop finalization | 91,772 ms | 92,735 ms |
| Actual native attempts | 25 | 32 |
| Whole-body checks, including initial baseline | 10 | 8 |
| Whole-body check time | 38,392 ms | 29,636 ms |
| Kernel commit time | 47,694 ms | 55,608 ms |
| Recognition time | 5,499 ms | 7,299 ms |
| Net face-count reduction | 219 | 251 |
| Original sample source tags removed | 10 | 14 |
| Final consistency faults | 0 | 0 |

The grouped run processed 28% more attempts and achieved 14.6% more net face
reduction in approximately the same time. Validation time fell 22.8% despite
the additional work. This is a bounded throughput observation, not a completed
4,361-face cleanup measurement. No real invalid group occurred in these timed
prefixes. Separate injected-invalid-group tests verified complete rollback,
strict replay retaining the good heal, and Stop finalization for both valid
and invalid pending groups. All trials restored the original 6,120 face tags
with undo, and neither original nor copy was saved.

Evidence: `artifacts/blend-grouped-d80aceca2bc44e64b5e647beb9a416d2/`
(`report.txt`, `report.syslog`, `verification.json`). The algorithm tested there
matches the final deployment build; the final build adds verification-stage
progress text and passed the native collector and production-window regressions.
Deployment hashes and the prior DLL/PDB backups are recorded under
`artifacts/blend-runtime/`.
