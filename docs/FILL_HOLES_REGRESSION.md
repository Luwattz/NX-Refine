# Fill Holes regression checklist

Run these checks in NX 2312 on a disposable part containing several cylindrical
holes, including holes on more than one solid body and at least one connected
multi-face counterbore region.

## Dialog and preview

1. Open **Fill Holes** and confirm the native dialog has a multiple **Target
   entities** selector, a **Max hole radius (part units; 0 = any)** input, and a
   lower multiple **Hole faces to fill** selector with OK, Apply, and Cancel.
2. Select one body, then a second body. Candidates from both bodies must be
   highlighted and remain in the lower collector.
3. Enter `0`. Cylindrical faces of every radius in the selected bodies may be
   preview candidates. Enter a positive radius and verify faces above that
   radius are not highlighted or selected.
4. Deselect one face in a connected counterbore region. The complete connected
   candidate region must leave the lower collector and preview. Use the native
   collector to restore it and verify it returns.
5. Cancel and reopen. The preview and selections must clear, with no geometry
   mutation.

## Commit and persistence

6. Apply on a disposable copy and verify only retained hole faces are healed;
   the dialog remains open and supports another body selection. NX Undo must
   reverse the complete multi-body pass.
7. Enter a positive radius, complete Apply or OK, reopen Fill Holes, and verify
   that the last valid radius is restored. A missing setting defaults to 4
   current-part units; `0` remains the explicit unlimited value.
