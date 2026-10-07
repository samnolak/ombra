# OMP GUI (Avalonia client) — third-party notices

OMP GUI is distributed under the MIT License (`LICENSE`). It is an independent client for the original omp
(oh-my-pi); it is not an official product of the omp authors, the Bun authors, Anthropic or OpenAI.

## Bundled in this package

| Component | Version | License | Source |
|---|---|---|---|
| .NET runtime (self-contained) | 10.0 | MIT | https://github.com/dotnet/runtime |
| Avalonia (+ Desktop, Skia, HarfBuzz, Win32, X11, Native, FreeDesktop, Themes.Fluent, Fonts.Inter) | 12.1.3 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Inter typeface (in Avalonia.Fonts.Inter) | — | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| ANGLE for Windows (Avalonia.Angle.Windows.Natives) | 2.1.27548 | BSD-3-Clause | https://chromium.googlesource.com/angle/angle |
| SkiaSharp, SkiaSharp.HarfBuzz (with Skia) | 3.119.4 | MIT (Skia: BSD-3-Clause) | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp (with HarfBuzz) | 8.3.1 | MIT (HarfBuzz: Old MIT) | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime | 0.11.6 | MIT | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol | 0.94.1 | MIT | https://github.com/tmds/Tmds.DBus |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Markdig | 1.4.0 | BSD-2-Clause | https://github.com/xoofx/markdig |
| Iciclecreek.Avalonia.Terminal | 4.0.2 | MIT | https://github.com/tomlm/Iciclecreek.Avalonia.Terminal |
| XTerm.NET | 2.0.2 | MIT | https://github.com/tomlm/XTerm.NET |
| Porta.Pty | 2.2.2 | MIT | https://github.com/tomlm/Porta.Pty |
| Microsoft.Windows.Console.ConPTY (conpty.dll, OpenConsole.exe) | 1.24 | MIT | https://github.com/microsoft/terminal |
| Unicode.net | 2.0.0 | MIT | https://github.com/neosmart/unicode.net |
| Wcwidth | 4.0.1 | MIT | https://github.com/spectreconsole/wcwidth |
| sherpa-onnx (org.k2fsa.sherpa.onnx + native runtime) | 1.13.8 | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime (inside the sherpa-onnx native runtime) | — | MIT | https://github.com/microsoft/onnxruntime |
| PortAudioSharp2 (+ native PortAudio runtime) | 1.0.6 | Apache-2.0 (PortAudio: MIT-style PortAudio license) | https://github.com/csukuangfj/PortAudioSharp2 |
| Avalonia.Controls.WebView (browser preview panel) | 12.1.0 | MIT | https://github.com/AvaloniaUI/Avalonia.Controls.WebView |

## Downloaded on first use (not in this package; each under its own license)

| Component | Version | License | Where from |
|---|---|---|---|
| omp (`@oh-my-pi/pi-coding-agent`) and its dependencies | 18.8.0 (pinned lockfile) | MIT (omp); dependencies: their own licenses, installed with their license files | npm registry |
| Bun (`@oven/bun-<platform>`) | 1.4.2 (pinned sha512) | MIT (Bun; JavaScriptCore / WebKit parts: LGPL-2.0) | npm registry |
| Dictation model: NeMo Parakeet TDT 0.6B v3 (int8, sherpa-onnx export) | pinned revision | CC-BY-4.0 (NVIDIA) | huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8 |

The dictation model is downloaded only after the user agrees, and the omp runtime only when the user installs it.

## Used from the system (not in this package)

The browser preview panel shows pages with the web engine the operating system provides; none is bundled: Microsoft Edge
WebView2 Runtime on Windows, WebKit (WKWebView) on macOS, WebKitGTK (`libwebkit2gtk-4.1`, LGPL-2.1) on Linux. Where
the engine is missing, the panel offers to open the page in the system browser instead.
