# Fill Holes regression checklist

Run these checks in NX 2312 on a disposable part containing several cylindrical
holes, including holes on more than one solid body and at least one connected
multi-face counterbore region.

## Dialog and preview

1. Open **Fill Holes** and confirm the native dialog has a multiple **Target
   entities** selector, a multiple **Seed inner hole faces** selector whose
   default intent is **Boss and Pocket Faces**, a **Max hole radius (part units;
   0 = any)** input, and a lower multiple **Hole faces to fill** selector with
   OK, Apply, and Cancel.
2. Select one body, then a second body. Pick an inside cylindrical ring on each
   body. NX must expand each seed with the native Boss/Pocket intent; only the
   corresponding cavity regions are highlighted and retained in the lower
   collector. Exterior cylindrical walls and unrelated bosses must stay out.
3. Enter `0`. The explicitly seeded hole regions may be preview candidates
   regardless of radius. Enter a positive radius and verify regions containing
   an inner cylinder above that radius are not highlighted or selected.
4. Deselect one face in a connected counterbore region. The complete connected
   candidate region must leave the lower collector and preview. Add another
   seed or use the lower collector to restore it and verify it returns.
5. Cancel and reopen. The preview and selections must clear, with no geometry
   mutation.

## Commit and persistence

6. Apply on a disposable copy and verify only retained hole faces are healed;
   the dialog remains open and supports another body selection. NX Undo must
   reverse the complete multi-body pass.
7. Enter a positive radius, complete Apply or OK, reopen Fill Holes, and verify
   that the last valid radius is restored. A missing setting defaults to 4
   current-part units; `0` remains the explicit unlimited value.
