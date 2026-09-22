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

13. On the 958-face `test_model.prt` housing, compare inward concave edge
    rounds with complete hole walls. At radius 4, the full-angle filter must
    retain 9 groups (three 4-face blind-hole groups and six single-face holes).
    Partial cylindrical arcs of roughly 70 to 90 degrees must not appear.
    Native Hole Faces can return those rounds as singleton rules, so inward
    normals and successful native rule creation alone are insufficient.

Verified in isolated NX 2512 runs: native-only recognition initially returned
42 groups; requiring full angular spans retained 9 groups, each identical to
its native Hole Faces result. Reports are in
`artifacts/fill-holes-diagnosis/housing-uv.txt` and `housing-filtered.txt`.
Split and interrupted hole walls are conservatively excluded by this check.
