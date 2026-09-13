# Incomplete loop recognition: expanded native options

The fixed-mode deletion policy below is superseded by
[native recognition retries](NATIVE_BLEND_RETRIES.md). Expanded recognition is
still the preview and preferred deletion mode; stalled deletion now tries the
other native recognition combinations.

## Finding

The user showed a local highlighted strip in the plugin but a complete closed fillet loop with manual Connected Blend Faces selection. The implementation called the single-argument `CreateRuleFaceConnectedBlend(seed)` overload. It did not explicitly enable recognition of blend-like or unlabeled blend surfaces, which can occur in imported or previously healed geometry.

The shared production rule factory now uses:

```csharp
CreateRuleFaceConnectedBlend(seed, true, true, null)
```

The two flags are `includeBlendLike` and `includeUnlabeledBlend`, as documented in the installed NX 2312 `NXOpen.xml` (`ScRuleFactory.CreateRuleFaceConnectedBlend` and `FaceConnectedBlendRule.GetDefiningData`). Both preview and the actual Delete Face builder use this one factory. Weak seed-radius semantics and manually deselected-face protection are unchanged. No fixed-face fallback was added.

## Evidence on the local baseline

- Default recognition: 524 initial seeds, 180 distinct chain results, 458 covered faces, largest chain 46 faces.
- Expanded recognition: the same 524 seeds, 143 distinct chain results, 524 covered faces, largest chain 65 faces.
- Independent probes: the 65-, 50- and 33-face chains all healed successfully, as did nine tested 15-face chains. Probes were undone individually and the part was never saved.
- Production execution logs confirm the actual live-rule builder committed the 65-, 50- and 33-face chains. This is separate from the exploratory fixed-list probes.
- The synthetic journal reads the production rule's defining data and asserts both flags are true. It also checks live native deletion, no radius gate after seed recognition, manual exclusion, solid preservation and undo. Passed.

The user's exact intermediate part and indicated face tags were not supplied. The baseline demonstrates a real recognition gap and a successful complete-chain correction; it does not establish a one-to-one match between a particular logged chain and the screenshot location.

Final production regression: **524 expanded faces, 442 actual/reported removals, 184 attempts, 120013 ms, budget reached**. Solid preservation, count accuracy and full undo restoration passed. The remaining 82 were not all exhausted. No input part was saved. Expanded recognition takes longer than the default rule (approximately 29 seconds versus 3 seconds for a full seed scan in independent probes), so complete coverage is prioritized over the earlier scan speed.
