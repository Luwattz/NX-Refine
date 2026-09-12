# Clear Cavities regression checklist

These checks require Siemens NX 2312 and should be run on disposable copies of the sample parts.

## Dialog and selection

- [ ] Geometry Cleanup contains **Clear Cavities** in the Simplify group and the command opens the native Block Styler dialog.
- [ ] Target entities accepts one or more solid bodies and rejects sheet bodies or assembly occurrences.
- [ ] The cavity collector is populated only after a body is selected; all detected cavity faces are highlighted and the selected body is not highlighted as a whole.
- [ ] A body with no fully enclosed cavity leaves the collector empty and does not show an error page.
- [ ] Deselecting any face in a connected cavity group removes the complete group from the preview; reselecting it restores the group.

## Detection

- [ ] A blind/open pocket connected to an exterior face is not offered as an enclosed cavity.
- [ ] A completely enclosed void bounded by a separate inner face shell is offered as one connected candidate group.
- [ ] Multiple bodies and multiple independent cavities are deduplicated and shown together.
- [ ] Imported/non-manifold or self-intersecting geometry is logged as a review case rather than silently treated as a cavity.

## Commit and cancellation

- [ ] **Apply** deletes and heals only retained cavity groups, keeps the dialog open, and refreshes the preview.
- [ ] **OK** performs the same edit and closes the dialog.
- [ ] **Cancel**, Escape, and closing the dialog clear highlights and leave geometry unchanged.
- [ ] A failed healing pass returns to the single NX undo mark created for that pass.
