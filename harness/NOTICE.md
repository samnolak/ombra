# Harness third-party notices

The harness profile template contains material adapted from these MIT-licensed projects. The adaptations are small
(skill names without the `superpowers:` prefix, narrower "when to use" descriptions, review through the `reviewer`
agent).

| Harness files | Source | Version |
|---|---|---|
| `skills/systematic-debugging/`, `skills/verification-before-completion/`, `skills/test-driven-development/`, `skills/requesting-code-review/`, `skills/receiving-code-review/` | [obra/superpowers](https://github.com/obra/superpowers) | v6.3.0 (`b36e082`) |
| `skills/karpathy-llm-wiki/` | [Astro-Han/karpathy-llm-wiki](https://github.com/Astro-Han/karpathy-llm-wiki) | `eafcc77` |

The `codebase-memory` MCP server uses the official release of
[DeusData/codebase-memory-mcp](https://github.com/DeusData/codebase-memory-mcp) v0.10.8 (MIT; its `LICENSE` and
`THIRD_PARTY_NOTICES.md` are in the release archive, next to the binary). The template does not redistribute it:
setup downloads it and checks it against the SHA-256 in [docs/harness.md](../docs/harness.md).
[Context7](https://mcp.context7.com) is used as a remote MCP service and is not part of this repository.

The `code-graph`, `handoff`, `library-docs` skills, `agent/AGENTS.md`, the `reviewer` and `task` agents and the
launch script are part of this repository (MIT, see the repository `LICENSE`).

---

## obra/superpowers

MIT License

Copyright (c) 2025 Jesse Vincent

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

## Astro-Han/karpathy-llm-wiki

MIT License

Copyright (c) 2026 Yuhan Lei

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
