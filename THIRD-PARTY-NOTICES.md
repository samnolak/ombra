# Third-party notices

This repository is distributed under the MIT License ([LICENSE](LICENSE)). Its desktop client (OMP GUI, in
[`avalonia/`](avalonia)) is an independent client for the original omp (oh-my-pi); it is not an official product of
the omp authors, the Bun authors, Anthropic or OpenAI.

- **Desktop client:** [`avalonia/packaging/THIRD-PARTY-NOTICES.md`](avalonia/packaging/THIRD-PARTY-NOTICES.md) lists
  every component bundled in its packages (.NET runtime, Avalonia, SkiaSharp / HarfBuzzSharp, CommunityToolkit.Mvvm,
  Markdig, the terminal and PTY libraries, sherpa-onnx, PortAudio, …) with version, license and source, what it
  downloads on first use (omp, Bun, the dictation model) and what it uses from the system (the web engine of the
  preview panel). Every package ships that file next to the app.
- **Harness profile template:** [`harness/NOTICE.md`](harness/NOTICE.md) covers the adapted skills (obra/superpowers,
  Astro-Han/karpathy-llm-wiki) and the codebase-memory-mcp release the profile uses.
