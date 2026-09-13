# Remove Blends regression: current native-rule implementation

The conservative region-safety experiment was reverted at the user's request.
The regression again checks native-chain deletion counts, current faces, solids
and undo, without whole-region atomicity or a local shape veto.

Current zero-removal investigation and native mode retries:
[NATIVE_BLEND_RETRIES.md](NATIVE_BLEND_RETRIES.md). The single-pass numbers below
are historical measurements of the previous fixed expanded mode.

Expanded loop recognition and flags are documented in [EXPANDED_BLEND_RECOGNITION.md](EXPANDED_BLEND_RECOGNITION.md). Current radius behavior and earlier evidence are documented in [WEAK_RADIUS_NATIVE_DELETE.md](WEAK_RADIUS_NATIVE_DELETE.md). Radius limits apply only to seed filtering; full live connected-face rules are submitted to Delete Face with Heal. Prior overlap-wide rollback and two-radius shape veto are superseded.

## Results

- Baseline: 524 initial seeds, 524 expanded preview faces, 442 actual/reported removals, 184 attempts, 120013 ms, budget reached. Solid body preservation and full undo restoration passed. Input was not saved.
- Synthetic native-rule test: deletion is not gated by radius after seed recognition; manual exclusion, solid preservation and undo passed.
- Release compiled against NX 2312; existing environment warnings remain.

## Reproduce

```powershell
./scripts/test-nx-remove-blends.ps1 -PartPath ./artifacts/blend-bench/model_baseline.prt -MinimumRemoved 350 -RepeatCount 3 -MinimumSecondRoundRemoved 1
& 'C:\Program Files\Siemens\NX2312\NXBIN\run_journal.exe' "$pwd\tests\RunNxBlendCollectorRegression.cs" -args "$pwd\bin\Release\NXRefine.dll"
```

The local baseline is an ignored fixture. Use `-CompileOnly` to compile without running NX. Failure/skip logs distinguish kernel deletion failure, post-check rollback, manual exclusion and budget limits. The previous 195/410-face results use different policies and are historical, not current acceptance targets.

