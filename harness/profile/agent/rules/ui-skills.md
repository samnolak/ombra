---
description: Load the matching design skill before the first UI file edit; review visual changes before replying
condition: ["*.tsx", "*.jsx", "*.vue", "*.svelte", "*.astro", "*.html", "*.css", "*.scss", "*.less", "*.axaml", "*.swift"]
---
You are about to change UI code. A pure logic, test or config change in these files needs no skill: continue.

Otherwise, if no design skill has been read in this session, first `read skill://<name>` of the matching one (at most two):

| Task | Skill |
|---|---|
| Landing, marketing, portfolio page; a page that looks generic | `design-taste-frontend` |
| Dashboard, admin, settings, tables, forms, design-system consistency | `interface-design` |
| Style, palette, fonts, chart type; UX or accessibility check | `ui-ux-pro-max` |
| Polish of components and details | `emil-design-eng` |
| New web animation (`motion` / `framer-motion` in the project → `motion`) | `animate` |
| React Native / Expo motion | `animate-expo` |
| Janky or slow animation | `animation-performance` |
| Gestures, springs, sheets, Apple-like feel | `apple-design` |
| Web app should feel native on a phone | `mobile-native` |
| Swift | `write-swift` |

After a change the user will see, and before the final reply, `read skill://ui-design-review` and pass it.
