# Repair Unattached Faces regression checklist

These checks require Siemens NX 2312 and should be run on disposable copies of the sample parts.

## Dialog and selection

- [ ] Geometry Cleanup contains **Repair Unattached Faces** in the Repair group and opens the native Block Styler dialog.
- [ ] Target entities accepts one or more solid bodies and ignores sheet bodies or assembly occurrences.
- [ ] The maximum gap field starts from the current Settings > Sew tolerance value and accepts decimal input in current part units.
- [ ] Typing a value such as `0.2` does not start a scan after the first `0`; Enter, leaving the field, or an action button commits it once.
- [ ] The candidate collector is populated only after a body is selected; candidate faces are highlighted while the selected body is not highlighted as a whole.
- [ ] Cancel, Escape, and closing the dialog clear all candidate highlights and leave geometry unchanged.

## Detection

- [ ] Repeating a native commit at the same gap, including after a scan with no candidates, does not repeat the scan. Changing bodies still rebuilds.
- [ ] Compare candidates before/after optimization on the same part and tolerance. The NX log reports scan milliseconds and distance/containment query counts; actual speed depends on topology.

- [ ] A face pair sharing a topological edge is not reported as an unattached gap.
- [ ] Faces touching only at a vertex, zero-distance contacts, same-facing surfaces, and back-to-back thin walls are excluded.
- [ ] A known small separation between a rib bottom and its base face is reported with only the smaller problem-side face highlighted.
- [ ] A known small local face-to-face gap is reported when its positive distance is within tolerance and a two-dimensional patch of opposing faces borders sampled empty space.
- [ ] Deliberate clearances greater than the tolerance are not reported.
- [ ] Multiple independent gaps sharing the same large support face remain separate groups. Only connected problem-side faces are grouped.
- [ ] A smaller tolerance rebuilds the candidate set and clears stale highlights from the previous value.

## Review and commit

- [ ] Deselecting any face in a connected candidate group removes the complete group from the preview and pending repair.
- [ ] **Apply** repairs only retained groups, leaves the dialog open, and refreshes the preview.
- [ ] **OK** performs the same repair and closes the dialog.
- [ ] Same-body gaps use Delete Face with Heal on the smaller candidate face; separate solid bodies use native solid Sew.
- [ ] A failed repair pass returns to the single NX undo mark created for that pass.
