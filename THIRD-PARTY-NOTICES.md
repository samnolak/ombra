# Third-party notices

This repository is distributed under the MIT License ([LICENSE](LICENSE)). Its desktop client (Ombra, in
[`avalonia/`](avalonia)) integrates omp (oh-my-pi), including project-maintained derivatives when shipped; it is
not an official product of the omp authors, the Bun authors, Anthropic or OpenAI. Fork distributions must retain
upstream copyright and license notices and identify their upstream base and Ombra revision.

- **Desktop client:** [`avalonia/packaging/THIRD-PARTY-NOTICES.md`](avalonia/packaging/THIRD-PARTY-NOTICES.md) lists
  every component bundled in its packages (.NET runtime, Avalonia, SkiaSharp / HarfBuzzSharp, CommunityToolkit.Mvvm,
  Markdig, YamlDotNet, the terminal and PTY libraries, sherpa-onnx, PortAudio, …) with version, license and source, what it
  downloads on first use (omp, Bun, the dictation model) and what it uses from the system (the web engine of the
  preview panel). Every package ships that file next to the app.
- **Harness profile template:** [`harness/NOTICE.md`](harness/NOTICE.md) covers the adapted skills (obra/superpowers,
  Astro-Han/karpathy-llm-wiki, Leonxlnx/taste-skill, emilkowalski/skills, nextlevelbuilder/ui-ux-pro-max-skill,
  Dammyjay93/interface-design, motiondivision/ai-kit), the codebase-memory-mcp release and the on-demand MCP servers
  the profile uses.
- **Owned agent engine sources:** [`runtime/engine/`](runtime/engine/) contains the shared Ombra host and
  the complete coding-agent source tree derived from omp 18.12.2, upstream base
  `1d5aff90b057ce7dbfe32ea557a527c1b3144b72`. The retained upstream MIT license is
  [`coding-agent/LICENSE`](runtime/engine/coding-agent/LICENSE); upstream third-party notices remain beside it.
  The generated pack manifest records the complete source digest; installed identity is
  `ombra-engine-18.12.2-bun-1.4.2-v1`, not an unmodified upstream distribution. This local derivative
  is not a separately published fork release or a claim that an upstream signing identity signed our changes.

## Lucide icons

The desktop client's line icons (`avalonia/src/Ombra.App/Views/Icons*.axaml`, written by
`avalonia/tools/icons/lucide.py`) are path data converted from [Lucide](https://lucide.dev) 1.57.0
(`lucide-static`, https://github.com/lucide-icons/lucide), under the ISC License below. The Lucide icons derived from
the Feather project (listed in Lucide's `LICENSE`, among them `arrow-*`, `at-sign`, `check`, `chevron-*`, `code`,
`download`, `external-link`, `hash`) are also under the MIT License below.

```
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

```
The MIT License (MIT) (for the Lucide icons derived from Feather)

Copyright (c) 2013-present Cole Bemis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
