---
name: ui-design-review
description: "Mandatory visual gate after UI edits: render, zoom, measure, designer review, fix."
---

# UI design review gate

The user must never be the first person to look at a visual result. A change that type-checks and "matches the request" is not done until it looks neat to a designer.

## When

After every change that alters what the user sees (layout, colour, size, icon, spacing, motion), before the final reply. Also when a user request is applied literally: literal is not the same as good.

## Steps

1. **Render the real surface.** Open the actual page (browser), reach the changed state with real data. Take:
   - a 1:1 screenshot in context (neighbours, the whole card/row/screen);
   - a zoomed crop (2–3×) of each changed element;
   - the worst realistic case (max items, longest text, overlaps, every tone/variant).
2. **Compare to the reference.** Reference = user's mockup/screenshot, Figma, or the previous state the user liked. Measure, don't eyeball: sizes as ratios (element / container), gaps on every side, overlap step, offsets. List each deviation.
3. **Independent designer review.** Spawn a `reviewer` subagent (fresh eyes, no stake in the code) with: the screenshot paths, the intent in one sentence, the reference and the measured numbers. Ask it to act as a senior UI/UX designer and return a `| Problem | Fix | Why |` table, blocking issues first. Checklist it must cover:
   - alignment, equal gaps, symmetry, concentricity;
   - proportions and hierarchy (nothing too big/small for its role);
   - separation: overlapping or adjacent elements stay distinct (rings, gaps, contrast) — no smudge;
   - legibility/contrast over photos and backgrounds;
   - consistency with the design system and with the rest of the screen;
   - noise: duplicate affordances, clutter, decoration without purpose;
   - clipping, cut-offs, distortion, blurry or misaligned icons.
4. **Fix and re-shoot.** Fix every blocking item, take the screenshots again, re-check. Repeat until the reviewer and you have no blocking items.
5. **Push back with evidence.** If the user's literal request makes the result worse (e.g. removing the ring that separates overlapping badges), still show it, but say in the reply what got worse, with the screenshot, and offer the better variant. Never ship a known-ugly result silently.

## Reply

State briefly what was checked (states, zoom, measured numbers) and what the review changed. Never claim "checked" without the screenshots having been looked at.
