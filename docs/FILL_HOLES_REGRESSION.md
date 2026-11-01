# Fill Holes regression checklist

Run in NX 2512 on disposable parts; these are runtime acceptance checks,
not results inferred from a successful build.

1. Confirm exactly three controls: target bodies, maximum radius, and one
   face collector. Select a body without clicking any individual face:
   candidates must appear automatically.
2. Use a blind counterbore with a floor, bottom fillet, cylindrical wall,
   entrance face and exterior cylindrical boss. Compare the native Delete
   Face Hole Faces result to the preview. No Boss and Pocket Faces
   expansion may add carrier or rib faces. A floor or entry
   face returned by Hole Faces must not cancel the entire group.
   The exterior boss and unrelated housing faces must remain unselected.
3. Repeat with a through hole, multiple bodies and a cylinder whose underlying
   axis origin is below its trimmed blind-hole floor.
4. Change radius from a large value to a small value and to zero (unlimited).
   Old highlights and collector contents must clear before the rebuild.
   Positive limits must reject groups containing larger inner cylinders.
5. Repeat with a tapered blind hole whose main wall is `Conical`, with a
   conical entrance chamfer and a non-analytic floor. The preview must contain
   the complete native hole group and must not contain the coaxial exterior
   cylindrical shell or either exterior end face. Radius filtering uses the
   largest trimmed conical radius, not only the radius at the UF reference
   station.
6. Deselect a face: its entire hole group must disappear. Commit unchanged
   radius text by moving focus: excluded groups must remain excluded.
7. Apply/OK must use precisely the retained faces; Undo must restore the pass.
   Apply keeps the dialog open, Cancel removes pending highlights, and
   completion/cancellation must not open the Listing Window.
8. Reopen after Apply/OK: the last radius must be restored.
9. If native hole recognition or expansion fails, verify the NX system log
   identifies the seed; no whole-body adjacency expansion may replace the
   failed rule.
10. Select the radius text, type `0.2` one character at a time, and verify that
   typing the first `0` does not scan or freeze NX. Press Enter or leave the
   field and verify that candidates rebuild once using `0.2`.
11. On NX 2512, `CreateRuleFaceHole` must receive options from
    `ScRuleFactory.CreateRuleOptions()`. Passing `null` produced an internal
    memory-access exception on every cylindrical seed in `test_model.prt`.
12. On `test_model_2.prt`, native Delete Face cannot heal the tested holes even
    when operated manually. The add-on must skip failed groups without an
    unhandled callback exception or claiming those holes were filled. A
    Boss/Pocket expansion covering 1743 of 1745 body faces must be rejected.

13. Historical primary-pass baseline: on the 958-face `test_model.prt` housing, compare inward concave edge
    rounds with complete hole walls. At radius 4, the full-angle filter must
    retain 9 groups (three 4-face blind-hole groups and six single-face holes).
    Partial cylindrical arcs of roughly 70 to 90 degrees must not appear.
    Native Hole Faces can return those rounds as singleton rules, so inward
    normals and successful native rule creation alone are insufficient.

Verified in isolated NX 2512 runs: native-only recognition initially returned
42 groups; requiring full angular spans retained 9 groups, each identical to
its native Hole Faces result. Reports are in
`artifacts/fill-holes-diagnosis/housing-uv.txt` and `housing-filtered.txt`.
Split and interrupted hole walls were conservatively excluded by this check.
The imported-wall fallback below is now evaluated separately; the historical
nine-group total is not an acceptance count for the combined scan.

## Early seed rejection regression (2026-10-31)

`FindGroups` now rejects a partial-angle seed before constructing a native
Hole Faces rule. The original region checks remain in place. An accepted
region must contain its seed, so this moves a necessary condition earlier
without changing which complete native regions can qualify. Native-rule
timings and skipped partial-seed counts are logged for large imported parts.

Compare exact group face-tag sets against the baseline DLL on the 958-face
housing and tapered/conical fixture. On rearLamp, compare the full candidate
set to the diagnosis-only early-filter result (84 groups / 112 faces), and
retain the small-body eight-hole healing and undo checks. Do not count every
rejected partial arc as a missed hole: concave edge rounds must stay excluded.

## Partial-success callback regression (2026-10-31)

The interactive rearLamp log reported 18 filled groups / 28 faces and 3
skipped groups, twice, followed by the partial-failure exception and dialog
cancellation. `Apply` returned an error whenever any group failed, despite
having committed successful groups. This prevented OK from being accepted;
subsequent dialog cancellation could restore those provisional changes.
The earlier independent-hole probes did not exercise this return path.

`Apply` now returns success when at least one group succeeds, after rolling
back only unsuccessful groups. If all groups fail it still returns failure.
Failed groups' empty undo marks are deleted by ID. The selection-list cleanup
in Reset is skipped in batch sessions, where NX SelectionManager is not
available; interactive selection behavior is unchanged.

`tests/NxFillHolesApplyRegression.cs` invokes the actual production Apply
callback using native NX geometry. It checks a successful group, a deliberately
stale duplicate group, then another successful group; all-success and
all-failed actions; retention after the Cancel callback; consistency and undo.
It explicitly models rejected-callback rollback and does not drive the
interactive Block Styler event loop. An isolated control DLL retains only
the old partial-error return, with the same batch guard, to reproduce the
bad return code without calling unsupported batch SelectionManager APIs.

```powershell
.\scripts\test-nx-fill-holes-apply.ps1 -PartPath 'C:\Users\chimp\Desktop\Geometry\rearLamp.prt' -BodyTag 39237
.\scripts\test-nx-fill-holes-apply.ps1 -PartPath 'C:\Users\chimp\Desktop\Geometry\rearLamp.prt' -BodyTag 39245 -AllReviewed
```

The first fixture needs at least two independently healable holes. The
`-AllReviewed` case targets the original rearLamp housing and expects 18
removed groups and 3 retained groups. Tags refer to the recorded isolated
part loads. These tests use unsaved copies and restore outer undo marks.

## Imported and split wall coverage

See [housing hole coverage](HOUSING_FILL_HOLES.md) for the six-arrow model,
face sets, geometry checks, closed-rim tolerance and negative corner-round
cases. `diagnose-nx-cleanup.ps1 -Mode holes-scan -ProbePath` accepts an
expectation file: a comma-separated line requires an exact group; a line
prefixed with `!` forbids any listed face in the result. Comment lines begin
with `#`. Assertions fail the native test run before the input is closed.
`@groups=29` additionally requires the exact group count. The housing
expectation file contains all 29 valid bore face sets and excludes the five
rounded rib-end groups, as well as the previously rejected corner rounds.

The Apply regression accepts `-GroupsPath` to avoid repeating an expensive
recognition pass when validating already recorded groups. `-ExpectedRemovedGroups`
checks the exact healed count; an all-success cached run additionally checks
one originally empty bore-axis point per group becomes solid. It still checks
callback acceptance, Cancel preservation, body consistency and outer undo.

## Batched deletion and Stop regression

See [performance and Stop](FILL_HOLES_PERFORMANCE.md). Verify a disjoint
all-success batch, a native batch failure followed by independent mixed
success, and stale/duplicate groups. The existing partial-success callback
contract must remain intact.

`-AllReviewed -GroupsPath ... -StopAfterSuccess` requests the same managed
stop signal as the progress button after a batch succeeds. No worker calls
NX APIs. The test requires completed fills, untouched remaining bore points,
retained pending groups, direct continuation through Apply without a new
recognition pass, Cancel preservation, body consistency and outer undo.
An active native computation may finish before the stop is observed.
