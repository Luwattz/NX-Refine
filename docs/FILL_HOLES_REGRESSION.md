# Fill Holes regression checklist

Run in NX 2312 on disposable parts; these are runtime acceptance checks,
not results inferred from a successful build.

1. Confirm exactly three controls: target bodies, maximum radius, and one
   face collector. Select a body without clicking any individual face:
   candidates must appear automatically.
2. Use a blind counterbore with a floor, bottom fillet, cylindrical wall,
   entrance face and exterior cylindrical boss. Compare the native
   Boss and Pocket Faces selection from the inner wall to the preview.
   The exterior boss and unrelated housing faces must remain unselected.
3. Repeat with a through hole, multiple bodies and a cylinder whose underlying
   axis origin is below its trimmed blind-hole floor.
4. Change radius from a large value to a small value and to zero (unlimited).
   Old highlights and collector contents must clear before the rebuild.
   Positive limits must reject groups containing larger inner cylinders.
5. Deselect a face: its entire hole group must disappear. Commit unchanged
   radius text by moving focus: excluded groups must remain excluded.
6. Apply/OK must use precisely the retained faces; Undo must restore the pass.
   Apply keeps the dialog open, Cancel removes pending highlights, and
   completion/cancellation must not open the Listing Window.
7. Reopen after Apply/OK: the last radius must be restored.
8. If native expansion fails, verify the NX system log identifies the seed;
   no whole-body adjacency expansion may replace the failed rule.
9. Select the radius text, type `0.2` one character at a time, and verify that
   typing the first `0` does not scan or freeze NX. Press Enter or leave the
   field and verify that candidates rebuild once using `0.2`.
