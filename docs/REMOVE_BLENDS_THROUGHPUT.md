# Remove Blends throughput and resumable deletion

The current deletion implementation amortizes whole-body checks in small,
undo-protected transactions; see [deletion runtime](REMOVE_BLENDS_RUNTIME.md)
for the evidence, rollback/replay policy and native regression.

The current interactive progress and continuous-run policy is described in
[progress and continuous cleanup](REMOVE_BLENDS_PROGRESS.md). The bounded
two-minute measurements below document the preceding release and remain useful
as a throughput reference; the UI no longer stops automatically at that limit.

Tested in NX 2512 on unsaved copies of `housing.prt` (one 6,120-face
solid; 4,181 radius-qualified source faces at 0–3 mm). Original SHA256:
`ED737FAFEB2784064DCEA3527FB210010BD36561C52E85422CB3E3C7F943A816`.

## Changes

- Reuse covered source members of a native connected region for preview;
  spherical sources still receive individual rules. A new unequal rule that
  expands a covered source triggers exhaustive recognition of all sources in
  the transitive overlap region. Overlapping rules are not blindly equated,
  clipped, or merged into a deletion operation. Keep all original sources for
  live deletion, including sources absent from their own rule's returned faces.
- Cache exact blend classification/radii and queried native-rule results during
  modal preview. Radius changes reuse queried sources; ownership queries are
  cached within immutable topology. Deletion invalidates these caches first.
- Recognize directly in the actual Delete Face builder, avoiding the old
  temporary collector followed by identical recognition in the builder.
- Keep pending source tags/modes across bounded Applies. Resolve current faces
  after every commit. Try fresh expanded sources before queued modes 1, 2, 0.
  A completed pass that made progress may start a fresh pass on changed geometry.
- Preserve attempted equal face sets throughout a pass, including across
  Applies. A successful unrelated edit no longer causes immediate repeated
  attempts on the same failed set. Clear the set at the next complete pass,
  so changed geometry can still enable earlier failures.
- Cache each body's validated consistency baseline during deletion. Current
  cleanup validates small undo-protected groups and replays a failed group
  with individual checks; earlier measurements below used individual checks.
- Provide a Stop window during deletion. Stop/budget exhaustion restores the
  surviving reviewed faces and manual exclusions. Apply resumes the pending
  queue without repeating Find. An unstarted or canceled commit stays retryable.
  All NX APIs remain on the NX thread; Stop waits for an active kernel call.

The existing 120-second / 256-attempt boundaries, complete live native rules,
weak radius policy, manual exclusions, full-chain removal, solid checks and
per-operation undo remain. No fixed face subsets or unions of overlapping
rules are used. Stop/Apply is incremental: accepted earlier edits remain under
the command's visible undo mark.

## Preview measurements

An evenly distributed 100-source sample returned exactly the same 76 complete
native groups in both versions:

| Measurement | Time |
| --- | ---: |
| Old first lookup | 17,443 ms |
| Cached implementation, first lookup | 16,986 ms |
| Same sources, cached lookup | 2 ms |
| Classification/radius queries, all 6,120 faces | 6,738 ms |
| Same classification/radii from cache | 16 ms |

These initial measurements isolate exact query caching before component reuse
was enabled. Their small first-lookup difference is not evidence of a general
initial-search speedup. Newly admitted sources still require native queries.

Evidence: `artifacts/cleanup-diagnosis-6cffac182fc146bd91a432352c4ba725/`.

The complete exhaustive reference scan evaluated all 4,181 seeds and returned
4,361 distinct faces in 782 native groups in 670,954 ms, including initial
filtering. It used exact ownership caching but no component reuse. Its exact
face-tag reference is `tests/data/housing-blend-preview.txt`; evidence is under
`artifacts/cleanup-diagnosis-e73f6d789df042d09a075fc511395242/`.

The component-preview candidate returned exactly the same 4,361 face tags in
171,014 ms (about 74.5% less time than the exhaustive reference). It queried
1,319 of 4,181 sources; 416 faces belonged to overlap regions requiring
exhaustive source checks. Its 775 retained representative native groups differ
from the exhaustive 782 because some redundant subsets are not queried;
the complete reviewed face selection is identical. This grouping is used for
preview only. Deletion always re-evaluates complete live native rules.
Evidence: `artifacts/cleanup-diagnosis-e1154b9548194443a96235003f1a2a0e/`.

The final DLL repeated the full scan in 183,681 ms during concurrent deletion
verification, with `PREVIEW_EXPECTED=4361 MISSING=0 EXTRA=0`, verified undo and
unchanged source hash. Final full-scan evidence:
`artifacts/cleanup-diagnosis-2de220dd67d745039137f1f8b5e018c4/`.
A distributed 100-source subset also matched the exhaustive selection; the
final algorithm took 10,182 ms versus 14,153 ms for the reference and 3 ms on
repeat. Evidence: `artifacts/cleanup-diagnosis-01c53e3b74a7497ea2750e418e937fa7/`.

## Deletion measurements and scope

Direct deletion benchmarks use the same sorted radius-qualified source list
and omit the expensive preview to isolate deletion throughput. Counts are
surviving source tags actually removed, not every healed/expanded face. The
normal UI still reviews full native expansion before deletion. Time-limited
counts and single-call overruns vary with kernel behavior and concurrent load.

The old sorted-source baseline removed 55 source faces in 127,505 ms (8 native
attempts). The pending-queue candidate removed 256 in 123,205 ms (34 attempts).
With the whole-pass failure cache, round one removed 292 in 120,337 ms
(38 attempts), and round two removed another 201 in 120,797 ms (34 attempts).
Actual/report counts matched; undo restored the original body and face tags;
the source hash was unchanged. Evidence:
`artifacts/cleanup-diagnosis-04315ab1d4144414b82f4ac34f0cd0bc/`.
These are
single-fixture observations, not a universal multiplier or a complete removal
rate. Some native chains remain unhealable.

The final DLL repeated two rounds: 292 source faces / 121,351 ms / 40 attempts,
then 201 / 122,380 ms / 37 attempts. Actual/report counts, solid checks, complete
undo and unchanged source hash passed. Final deletion evidence:
`artifacts/cleanup-diagnosis-5470c86132444ac1bda31eaab84ef3b0/`.
The tested DLL/PDB were copied to `bin/Release` and `deploy/application`;
`artifacts/blend-throughput/deployment.json` records the deployed hashes and
reports. Restart NX to load the update. The source part was never saved.

Rejected experiments included mode 0 first (0 removals and a 346-second
overrun), global All Blend replacement (73 source faces missing and 330
additional nonspherical faces), treating all overlapping regions as identical,
and batching native rules without source reuse (no meaningful lookup gain).
The specialized FaceEdgeBlend probe did not rescue the tested failed chains;
it is not used by production deletion.

## Regression

`scripts/test-nx-blend-collector.ps1 -PluginPath <candidate>` exercises all
four live native modes, weak radius, manual exclusions, complete preview after
an exhausted deletion budget, solid preservation and undo. It also checks stop
before recognition/commit, reviewed-selection restoration, resume, and removal
of canceled operations from the failure cache. No part is saved.

`scripts/diagnose-nx-cleanup.ps1 -PartPath <part> -Mode blend-preview-compare
-MaximumGroups 100 -PluginPath <baseline> -ProbePath <candidate>` compares
complete groups and cached classifications in the same NX process. The harness
copies the input, verifies undo restoration, and checks the original file hash.

`-Mode blend-preview-components-compare` compares the component algorithm's
preview union against exhaustive native rules on distributed source subsets.
`-Mode blends-fast-scan -ProbePath tests/data/housing-blend-preview.txt` verifies
the complete housing selection against the exhaustive reference, rejecting
both missing and extra faces.
