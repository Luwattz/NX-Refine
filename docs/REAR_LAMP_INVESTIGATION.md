# rearLamp cleanup investigation

Investigated on 2026-10-31 using the installed NX 2512.1700 and the current
production source, built into an isolated output directory. The reported
symptom was missing recognition or incomplete removal. The exact faces and
body selection from the user's interactive run were not supplied, so the
results below identify reproduced mechanisms rather than an exact replay of
that selection.

The original `C:\Users\chimp\Desktop\Geometry\rearLamp.prt` was copied into
`artifacts/rearLamp-investigation/`. All geometry operations used disposable,
unsaved copies. Completed runs restored and verified their outer undo mark.
The source part SHA256 before and after the investigation is:

```text
2967AD21F427FA345CDB521D4C0663FF25C82F2449505791F55D34D732187CC8
```

## Applied fix (2026-10-31)

The following changes were subsequently authorized and implemented. The
investigation and baseline measurements below describe the earlier code.

- Fill Holes performs its existing partial-angle seed rejection before native
  recognition. Region validation and native deletion boundaries are unchanged.
  Logs include skipped seed counts, native call counts, total body scan time
  and individual native recognition calls lasting at least one second.
- Remove Blends deletion streams one complete native chain at a time, checks
  its budget before each recognition seed, and restarts recognition on current
  topology after each successful commit. It no longer materializes/sorts the
  entire mode's chain list before attempting the first chain. Preview remains
  complete, even after a previous deletion exhausted its budget. Native rules,
  manual exclusions, body checks and rollback remain in force.
- Framework-based test executables now use NX's matching Framework loader.
  `scripts/test-nx-blend-collector.ps1` runs the extended native-rule regression.

Validation:

- Synthetic NX regression passed: all four recognition modes, weak seed
  radius, manual exclusions, streamed deletion, budget stopping before further
  recognition, complete preview after budget exhaustion, solid body and undo.
- The 958-face housing retained exactly the same 9 face-tag groups; scan time
  changed from 12,745 ms to 1,874 ms in the baseline/fixed isolated runs.
- The conical fixture retained its identical 3-face native hole group.
- rearLamp retained the exact experimental candidate set: 84 groups / 112
  faces across all 79 solids. Its reflector scan took 29 ms; the housing still
  took 99,817 ms. The latter contains many slow native calls, so large-model
  recognition and native healing limitations are not claimed to be eliminated.
- The fixed DLL also healed all eight reviewed groups on body 39237 in
  independent probes, retaining 79 solids and zero consistency faults. Undo
  and the source part SHA256 check passed. The harness now preserves the NX
  system log before the batch session can delete its temporary log on exit.
- The white-lens repeated run removed 4 faces in 121,211 ms / 121 attempts,
  then 5 faces in 120,161 ms / 125 attempts. Reported counts matched current
  topology and outer undo restored the input. Both rounds reached the budget.
  These time-limited counts differ from the earlier 5/5 run because ordering
  changed and elapsed-time cutoffs vary; this fix does not claim an increase
  in native healing success or complete removal of the lens's blends.

The initial performance-fix DLL and PDB were copied to `bin/Release/` and
`deploy/application/`. The deployed DLL hash matches the tested DLL:

```text
9DF38606E1D4DCBED3CD60F755E6E90992F6997FFAA183B05D545FFB967CDEE8
```

The previous deployment is backed up under
`artifacts/cleanup-fix/deploy-backup/`; `deployment.json` records the deployed
path, hash, timestamp and checks. The active NX custom-directory file points
to this repository's `deploy` directory. Save work and fully restart NX to
load the replaced assembly; no further compilation is needed.

### Follow-up: partial-success OK/Apply commit

The user's subsequent NX session log (`chimp51c4be26.syslog`) showed the same
housing operation twice: 18 filled groups / 28 selected faces, 3 skipped
groups, followed by a partial-failure exception and dialog cancellation. The
old Apply callback returned an error if **any** group failed. Consequently
the OK action was not accepted despite successful heals; cancellation could
roll those provisional changes back. The earlier per-hole tests did not
exercise this callback return path.

The callback now accepts the action if at least one group succeeded. Failed
groups are still individually rolled back and their empty undo marks are
deleted by ID. An all-failed operation still returns failure. Interactive
selection cleanup is unchanged; the unavailable SelectionManager call is
skipped in batch mode so the actual callback can run in native NX regression
tests without an interactive selection session.

The regression invokes the actual production Apply/Cancel methods with real
NX geometry, rather than testing only `DeleteReviewedHoleFaces`. A small
fixture attempts a successful hole, the now-stale same group, then another
successful hole. The fixed callback returns 0 with two groups still removed;
an isolated old-return-code control returns 1 with the same two groups
removed, and a simulated rejected-action rollback restores both. All-success,
all-failed, preservation after accepted Apply/Cancel, and undo cases pass.
This tests the callback return contract; it does not automate the interactive
Block Styler event loop.

On the original rearLamp housing, the full callback returned success with 18
groups removed and 3 retained. Accepted Cancel preserved the changed geometry.
The topology lost 34 original face tags in total: the logged 28 faces are
the reviewed deletion inputs, while healing can also merge adjacent faces.
These counts describe different things and must not be equated.

Evidence and the old deployed DLL are under
`artifacts/fill-holes-commit-fix/`. The new reusable runner is
`scripts/test-nx-fill-holes-apply.ps1`; the callback fixture and UI testing
scope are documented in `docs/FILL_HOLES_REGRESSION.md`.

The final housing run checked body consistency and solid counts before
restoring its outer undo mark; both passed with the 18 groups still removed.
The tested DLL/PDB were deployed on 2026-10-31 at 13:06 Singapore time. The
current DLL SHA256 is:

```text
B2D122465F42F786EADEE3A2DD7ECCEBE2F032F2D4BCDD0509FFCA3731DA13C5
```

`artifacts/fill-holes-commit-fix/deployment.json` records this deployment and
its previous-DLL backup. The original part SHA256 remains unchanged.

Evidence is in `artifacts/cleanup-fix/` and the fixed rearLamp reports copied
there after completion. No original part is saved by these tests.

## Findings

| Area | Reproduced evidence | Meaning |
| --- | --- | --- |
| Selection scope | 137 bodies: 79 solids, 58 sheets | Both dialogs deliberately accept solid bodies only. Sheets are outside their cleanup scope. |
| Blend recognition | 11,263 radius-0-to-3 seeds across all solids; 598 additional recognized blends above radius 3 | This part does contain recognizable blends. A missing result is not explained by a universal failure to recognize imported geometry. |
| Blend deletion | White lens: 75 seeds, 90 preview faces, 34 distinct native chains; first pass removed 5 faces in 120,030 ms / 112 attempts | Recognition succeeds, but native healing failures and the operation budget leave most preview faces behind. |
| Native failure | NX error 671147: remaining faces cannot close the area after deletion | Highlighting a native connected chain does not establish that Delete Face can heal it. |
| Hole recognition | 3,423 analytic cylindrical/conical faces; 1,497 pass the inner-wall test; 99 of these have full angular spans, 90 also meet radius 4 | The single-face 360-degree condition rejects most inward analytic arcs before they can become accepted groups. These counts are faces, not physical holes. |
| Supported holes | Production DLL on body 39237: 8 groups recognized; all 8 independently deleted and healed; 79 solids retained, consistency faults stayed 0 | Fill Holes is operational on supported geometry in this exact file. These probes were individually undone, not a sequential whole-model cleanup. |
| Recognition cost | Reflector: 54,150 ms to return zero groups with current production code | A zero preview can follow a lengthy scan even when no hole qualifies. |

### Remove Blends: distinguish preview, healing and budget

The white lens is the body whose imported name ends in `LENS/白色`, tag
39250 in these isolated loads. It has 207 faces. Seed recognition took part
in a 7,851 ms preview scan. Native expansion added faces beyond the initial
75 seeds, as intended by the existing weak-radius policy.

The first deletion pass reached the 120-second budget after 112 commit
attempts, removing only 5 of the 90 preview faces. The next preview had 71
seeds, 85 faces and 30 native chains. The second deletion pass removed another
5 faces in 120,257 ms / 114 attempts and also reached the budget. Undo then
restored the original topology. Both preferred mode 3 and fallback mode 0
were attempted. Repeated native failures identify unsuccessful heal attempts
on the current topology, not simply a radius-threshold problem. They do not
prove that every remaining chain is impossible to heal after other changes.

The 120-second / 256-attempt limits apply to best-effort deletion. Initial
preview recognition has no corresponding time budget. In addition,
`DeleteBestEffort` calls `BuildChainsForMode` for the entire surviving seed
pool before checking the deletion budget again. A single native operation
also cannot be interrupted by those between-operation checks. Thus 120
seconds is not a hard upper bound for the whole command.

The initial all-solid blend preview did not finish before its diagnostic
process was stopped; it had not reached the deletion stage. No complete
all-solid removal count is claimed.

Relevant source: `RemoveBlendsDialog.TryGetBlend`, `BuildChainsForMode`,
`DeleteBestEffort`, `TryDeleteChainForMode`, and `DeleteBudgetAvailable`.

### Fill Holes: filtering policy and expensive native recognition

`FindGroups` starts a native Hole Faces rule for each analytic seed before
applying `HasFullAngularSpan`, radius, inward-normal and coaxial checks to
the returned region. Because the region must contain the seed and every
analytic member must pass the angular check, a partial-span seed can never
produce an accepted region under the current policy.

A diagnosis-only DLL moved this **same existing angular check** ahead of
native recognition. It did not relax the check or change deletion behavior.
The experiment remains under `artifacts/`; production source and deployment
were not changed.

| Scope | Current production | Early angular rejection experiment |
| --- | --- | --- |
| Reflector, 2,892 faces | 0 groups, 54,150 ms | 0 groups, 28 ms |
| Small wire body 39237, 33 faces | 8 groups, 127 ms | Same 8 groups and tags, 74 ms |
| Bezel 39249, 4,970 faces | Original exhaustive scan interrupted before completion | 6 groups, 10,921 ms |
| Housing 39245, 6,120 faces | No completed baseline scan | 21 groups, 97,151 ms |
| All 79 solids | No completed baseline scan | 84 groups / 112 faces, 112,853 ms total recognition time |

Only the first two rows have measured baseline/result parity. For the
remaining rows, equivalence follows from moving a necessary seed condition
earlier in the same algorithm; full runtime parity has not been measured.
The housing still dominates even after this early rejection, so it needs
per-seed native timing before claiming the performance problem is solved.

The 1,398 inward faces that fail the angular condition include concave edge
rounds. They must not all be treated as missed holes. Eight partial-arc native
rules sampled on the bezel each returned a singleton. Three could be healed
and five failed both Face and Hole modes. The successful deletions still do
not prove those arcs are complete holes. This reproduces why merely dropping
the angular check or accepting anything healable would be unsafe.

An additional housing probe looking specifically for inward half-turn walls
with radii 1 through 4 found no matching seeds; it does not establish a
part-specific split-hole recovery result. Split, interrupted and non-analytic
hole walls remain a known coverage limitation of the current algorithm,
not a demonstrated count of missed physical holes in this investigation.

Relevant source: `FillHolesDialog.FindGroups`, `FindNativeHoleFaces`,
`HasFullAngularSpan`, `TryGetInnerRevolvedFace`, and
`DeleteReviewedHoleFaces`.

## Recommended follow-up

1. Move necessary seed checks ahead of native hole recognition, then confirm
   candidate-tag parity on the existing housing and conical-hole fixtures.
   This addresses measured wasted work without broadening deletion scope.
2. Measure native rule time per seed and allow recognition to stop between
   seeds in both preview and deletion regrouping. Report recognition,
   healing failures and budget exhaustion separately.
3. Keep native-chain semantics and manual exclusions for blends. Do not
   interpret a larger radius or unlimited runtime as a solution to error
   671147. Review failed chains with NX Delete Face on the same local region.
4. To broaden hole coverage, establish connected, coaxial, closed angular
   coverage across split walls while retaining native region boundaries.
   Validate against real split-hole fixtures and concave-round negative
   fixtures before enabling it. A successful heal alone is insufficient.
5. Process a focused solid body at a time on this model. Explicitly explain
   sheet-body exclusions and why each candidate group was rejected or skipped.

## Evidence and reproduction

Ignored evidence under `artifacts/rearLamp-investigation/`:

- `audit.txt`: all-solid face types, blend radii and analytic-wall filters.
- `holes.txt`: interrupted original-order scan, including the reflector timing.
- `holes-early-filter.txt`: completed 79-solid experimental recognition scan.
- `holes-small-body.txt`: production recognition, eight isolated heals, consistency and undo.
- `hole-probes.txt`: eight partial-arc native rule/healing probes and undo.
- `split-hole-probes.txt`: housing half-turn probe with no qualifying seeds.
- `blends.txt`: interrupted all-solid preview.
- `blends-lens.txt` and its NX system log: two consecutive white-lens passes followed by undo.
- `FillHolesDialog.EarlyFilter.cs` / `NXRefine.EarlyFilter.dll`: diagnosis-only experiment.

The reusable harness uses the real private geometry methods without opening
Block Styler dialogs. The default mode only audits. `holes` and `probes`
perform independently undone deletion probes; `blends` runs two consecutive
production passes and then restores the outer undo mark. A body tag is valid
only for the reported load; inspect the audit output if it changes.

```powershell
.\scripts\diagnose-nx-cleanup.ps1 -PartPath 'C:\Users\chimp\Desktop\Geometry\rearLamp.prt' -Mode audit
.\scripts\diagnose-nx-cleanup.ps1 -PartPath 'C:\Users\chimp\Desktop\Geometry\rearLamp.prt' -Mode holes -BodyTag 39237
.\scripts\diagnose-nx-cleanup.ps1 -PartPath 'C:\Users\chimp\Desktop\Geometry\rearLamp.prt' -Mode blends -BodyTag 39250
```

The script compiles a Framework executable and starts it with NX's
`run_dotnet_nxopen.exe`. NX 2512's C# journal compiler uses `managed_core`;
loading this Framework harness through a C# journal with `Assembly.LoadFrom`
caused an NXOpen session type-cast failure before geometry opened. That
diagnostic startup failure is separate from the cleanup findings.

Build validation succeeded with existing missing-.NET-4-targeting-pack and
deprecated StringBlock callback warnings. The pre-existing local edit to
`Core/SelectionScan.cs` was preserved. No cleanup production code or deployed
DLL was changed by this investigation.
