# housing Remove Blends optimization

Fixture: `C:\Users\chimp\Desktop\Geometry\housing.prt`, tested on unsaved
copies in NX 2512. The input has one 6,120-face solid and 4,181 radius-filtered
seeds at the tested 0-to-3 mm range. SHA256:

```text
ED737FAFEB2784064DCEA3527FB210010BD36561C52E85422CB3E3C7F943A816
```

After a successful native deletion, the previous scheduler restarted at the
first surviving seed. A difficult prefix was repeatedly recognized and tried
after every small successful edit, consuming time before later chains could
be reached. The new scheduler rotates past that successful seed, resolves
fresh faces, and continues. Earlier failures can be retried when the queue
wraps around. Native chain boundaries, exclusions, healing, solid checks and
rollback are unchanged.

The initial complete-preview tests did not finish before they were stopped,
before any deletion. Comparison therefore uses `blends-direct`: identical
radius-filtered source seeds, no expensive upfront preview, and two production
`DeleteBestEffort` invocations. That method still recognizes and commits full
live native rules. This is a deletion-scheduling benchmark, not an end-to-end
interactive command benchmark. In older report output, the direct-mode
`PREVIEW`/`GROUPS` fields denote the seed input, not actual preview/native-chain
counts. The harness now labels these `SEED_INPUT`/`INPUT_BATCHES`.

The four-mode synthetic regression passed, including weak radius, manual
exclusions, streamed native deletion, budget boundaries, complete subsequent
preview, solid preservation and undo. Evidence, the previous deployment and
the staged optimized DLL are under `artifacts/housing-blend-optimization/`.

First-round deletion results (same seed input and unchanged 120-second /
256-attempt limits):

| Version | Removed seed faces | Actual count | Attempts | Delete time |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 83 | 83 | 21 | 120,147 ms |
| Rotating seed order | 210 | 210 | 18 | 124,257 ms |

These counts refer to radius-filtered seed tags removed from current
topology, not all faces affected by native expansion/healing. The optimized
run removed about 2.53 times as many seed faces in this invocation. Individual
NX calls can exceed the between-call time limit. These are single-fixture,
time-limited measurements with concurrent isolated test processes; they are
not a general speed multiplier. Complete-preview latency and unhealable
native chains remain limitations.

| Second invocation | Removed seed faces | Actual count | Attempts | Delete time |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 67 | 67 | 22 | 124,485 ms |
| Rotating seed order | 31 | 31 | 22 | 121,590 ms |

Across two invocations the baseline removed 150 seed faces in 244,632 ms;
the optimized version removed 241 in 245,847 ms (about 61% more at similar
total deletion time). The second optimized pass removes fewer because its
first pass leaves a different remaining model; this is not a claim of a
per-invocation improvement in every state. Reported counts matched actual
topology in both runs; original topology was restored by undo and input
hashes remained unchanged. Per-operation native solid/consistency checks
remained active throughout.

The tested DLL/PDB were deployed to `bin/Release` and `deploy/application`.
`artifacts/housing-blend-optimization/deployment.json` records hashes and
deployment time; `baseline/` retains the previous DLL/PDB. Restart NX to load
the update. The original part was not saved or replaced.
