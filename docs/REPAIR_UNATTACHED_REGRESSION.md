# Repair Unattached Faces regression checklist

These checks require Siemens NX 2312 and should be run on disposable copies of the sample parts.

## Dialog and selection

- [ ] Geometry Cleanup contains **Repair Unattached Faces** in the Repair group and opens the native Block Styler dialog.
- [ ] Target entities accepts one or more solid bodies and ignores sheet bodies or assembly occurrences.
- [ ] The maximum gap field starts from the current Settings > Sew tolerance value and accepts decimal input in current part units.
- [ ] The candidate collector is populated only after a body is selected; candidate faces are highlighted while the selected body is not highlighted as a whole.
- [ ] Cancel, Escape, and closing the dialog clear all candidate highlights and leave geometry unchanged.

## Detection

- [ ] A face pair sharing a topological edge is not reported as an unattached gap.
- [ ] A known small separation between a rib bottom and its base face is reported with both sides highlighted.
- [ ] A known small local face-to-face gap is reported when its minimum distance is within the entered tolerance.
- [ ] Deliberate clearances greater than the tolerance are not reported.
- [ ] Multiple independent gaps are grouped separately; pairs sharing a face form one connected candidate group.
- [ ] A smaller tolerance rebuilds the candidate set and clears stale highlights from the previous value.

## Review and commit

- [ ] Deselecting any face in a connected candidate group removes the complete group from the preview and pending repair.
- [ ] **Apply** repairs only retained groups, leaves the dialog open, and refreshes the preview.
- [ ] **OK** performs the same repair and closes the dialog.
- [ ] Same-body gaps use Delete Face with Heal on the smaller candidate face; separate solid bodies use native solid Sew.
- [ ] A failed repair pass returns to the single NX undo mark created for that pass.
