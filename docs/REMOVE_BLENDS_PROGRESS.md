# Remove Blends progress and continuous cleanup

The interactive command no longer returns just because 120 seconds or 256
attempts have elapsed. Those limits were a major reason a 4,361-face preview
produced only roughly 200–300 removed faces per Apply. Bounded defaults remain
for diagnostics; interactive Apply explicitly enters continuous mode and
finishes when the native queue makes no further progress, or the user stops it.

All original source tags and fallback native modes remain queued in the existing
source order. A representative-first scheduling experiment did not demonstrate
reliable throughput improvement and is not enabled. Every actual deletion
still recognizes a complete live rule and honors manual exclusions, solid-body
checks, consistency validation and independent rollback. It does not guarantee
that NX can heal all selected faces.

Temporary `FaceConnectedBlendRule` wrappers implement `IDisposable`; preview
disposes them after destroying their temporary collector, and deletion disposes
them after destroying the builder. Undo can already invalidate a rule, which
the cleanup handles. Progress bookkeeping uses an incrementally maintained
pending-source set and updates deleted counts only after validated edits,
avoiding a full-candidate scan for each progress update.

The progress window displays:

- Stage 1/3: inspected faces / total faces during radius/classification filtering.
- Stage 2/3: checked or covered source faces, identified faces, and current chain
  recognition. Ambiguous overlaps require individual source checks.
- Stage 3/3: displaying the final selected faces; indeterminate bar while NX
  updates selection/display.
- Cleanup: actual removed reviewed faces / initially reviewed faces and attempted
  chains. This percentage measures deletion, not a promise every face is healable.
- Elapsed minutes/seconds update independently on the progress UI thread, even
  during a long NX call. All NX API calls remain on the NX thread.

Stop validates pending transaction edits (or rolls them back), preserving
completed cleanup and pending reviewed faces. Apply resumes the queue.
The current [deletion runtime](REMOVE_BLENDS_RUNTIME.md) implementation shares
whole-body checks across small transactions and replays invalid transactions
with individual checks. A single native kernel call cannot be forcibly interrupted by this
managed stop control; the window indicates it is waiting while time continues.

## Verification

`scripts/test-nx-blend-collector.ps1 -PluginPath <candidate>` now checks numeric
progress publication, continuous mode bypassing the diagnostic attempt limit,
and Stop still canceling before a native commit. Existing native deletion,
all four modes, weak radius, manual exclusion, complete preview, solid and undo
regressions remain active.

`scripts/test-selection-progress.ps1 -PluginPath <candidate>` renders the actual
production form offscreen for filtering, recognition, display, cleanup and
stopping. It checks text/bar/button bounds, determinate versus marquee behavior
and Stop state. The generated images are visually inspected.

`scripts/diagnose-nx-cleanup.ps1 -PartPath <part> -Mode blends-continuous-preview
-DeleteSeconds 360 -PluginPath <candidate>` performs the normal component
preview, then runs continuous deletion
for six minutes before requesting Stop. It works on a copy, validates actual
removal counts and per-operation body consistency, restores original topology
with undo, and verifies the source file hash. Timing excludes the preceding
preview in the deletion measurement. Pending/unhealable geometry remains.

On the housing fixture, a source-order continuous prototype removed 575 of
4,181 seed faces in 361,302 ms before the six-minute Stop request took effect
(111 native attempts; diagnostic budget not reached). Actual counts, solid
checks, original-topology undo and unchanged source hash passed. This confirms
the command can continue past the preceding two-minute cutoff; it is not a
claim of increased per-minute throughput or removal of every candidate.
Evidence: `artifacts/cleanup-diagnosis-256c87bec1ae4cff86e0328747db185a/`.

The representative-first experiment with a full preview removed 402 reviewed
faces in 365,639 ms; differing preview/load conditions prevent a controlled
speed comparison. It did not establish a reliable improvement and was removed.
Evidence: `artifacts/cleanup-diagnosis-80e18fe0a0e04821a2c459cc83687597/`.

The rule-disposal build's exhaustive-reference check retained all 4,361 faces
with zero missing/extra selections and verified undo/source hash. A bounded
large-part deletion check also passed actual counts, per-operation consistency
and undo. Evidence is under `artifacts/cleanup-diagnosis-2e5144b2d2e04076a8610c3ef86f3433/`
and `artifacts/cleanup-diagnosis-541354daa452495fbe208e63b997bcbe/`.

The final source-order DLL passed native regression and offscreen production
window layout tests, and is deployed to `bin/Release` and `deploy/application`.
`artifacts/blend-continuous/deployment.json` records its hash and evidence.
Restart NX to load the update; no source part was saved.
