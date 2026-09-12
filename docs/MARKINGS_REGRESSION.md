# Remove Markings regression checks

Run these checks in NX 2312 on a disposable part containing a planar carrier,
disconnected lettering at several heights, and taller bosses. This is a manual
integration checklist: compilation alone cannot verify native selection repaint.

## Input, preview, and selection

1. Open Remove Markings and confirm that there is one merged **Carrier faces and
   candidates** collector. Select a carrier and confirm the default maximum is 2 mm.
2. Replace the maximum (in mm) with 20, then 2, then 5, then 2 without pressing Enter or
   clicking Find Candidates. Pause briefly after each edit. The status maximum,
   collector count, and highlighted faces must all reflect the new threshold.
3. During an edit, the previous candidate collection and highlights must clear.
   OK and Apply must be disabled until a valid preview is ready. After rebuilding,
   both must become available without an extra click on the face collector.
4. Verify that lowering the threshold removes tall-boss highlights and that
   increasing it includes qualifying connected characters. The carrier and
   candidates must remain in the same collector. Only candidates may be highlighted;
   carrier faces must return to their normal display state after selection.
5. Enter invalid text, zero, a negative value, and an empty string. No previous
   candidates may remain actionable. Restore a valid positive value and verify
   that candidates and button availability recover.
6. Add a second carrier face and verify that its candidates are appended and
   highlighted without losing the first carrier. Deselect one carrier and verify
   that candidates are rebuilt for the carriers that remain.
7. Deselect a connected character. All its faces must leave the collector and
   preview. Focusing the height field without changing it must not restore it.
   Find Candidates / Restore All must restore it.
8. Cancel and reopen. The old preview must clear and the new dialog must start
   with no carrier or candidates. Move the pointer off the model to distinguish
   NX's red hover preselection from an actual retained highlight.
9. Confirm that no status or explanatory text is displayed below the Find
   Candidates / Restore All button.

## Destructive checks (disposable copy only)

- Immediately after a height edit, attempt Apply. A stale preview must never be
  deleted: the operation must be blocked or request review of a refreshed preview.
- Exclude a connected character, apply, and verify that the excluded character
  and carrier remain. Only the retained snapshot is passed to Delete Face with
  healing. NX can modify surrounding topology while healing.
- Undo the pass and verify restoration. Test OK and Cancel separately, including
  cancellation after a successful Apply; earlier applied passes remain until Undo.
- No completion/cancellation Listing Window should open. Failed healing must
  roll back and require a fresh scan.

## Local validation, 2026-09-12

On the local metric test part, the live non-destructive sequence produced:

| Maximum (mm) | Groups | Faces | Visible result |
|---|---:|---:|---|
| 20 | 12 | 130 | Lettering and tall bosses highlighted |
| 2 | 5 | 53 | Tall bosses and taller characters excluded |
| 5 | 8 | 88 | Three taller characters included; tall bosses excluded |
| 2 again | 5 | 53 | Same preview as the first 2-unit pass |

The sequence was performed without Enter. Invalid text cleared all candidates
and disabled OK / Apply. These counts are fixture-specific, not universal expected
counts. The private part and screenshots are not redistributed. Deletion/healing
was not exercised in this live validation.
