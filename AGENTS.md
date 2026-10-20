# Temporary files

- Screenshots, recordings and scratch images go to `.temp/` in this repo (gitignored), never the system temp folder.
  omp's browser screenshots land in `.temp/screenshots` (`browser.screenshotDir` in the local `.omp/config.yml`);
  for test screenshots set `OMBRA_SHOT_DIR` / `OMBRA_REVIEW_DIR` to a folder under `.temp/`.
- Before finishing a task, delete what you put in `.temp/`, unless the user asked to keep it (then say where it is).
- Tests clean up after themselves: a run writes every temp file under `<temp>/ombra-tests/run-<pid>`, deleted on exit,
  and the next run deletes folders of runs that were killed (`avalonia/tests/Ombra.Tests/TestEnvironment.cs`). Tests
  create temp folders with `TestProcesses.TempDir`, not the system temp folder directly.

# UI tests

- A test that makes a `MainViewModel`, a control, a `DispatcherTimer` or anything else that reads `Dispatcher.UIThread`
  is `[AvaloniaFact]` / `[AvaloniaTheory]`. On an xunit worker it makes that worker Avalonia's UI thread whenever no UI
  test runs (Avalonia 12 gives the UI dispatcher to the first thread that asks), and the UI test starting at that
  moment fails with `[Test Case Cleanup Failure (…)] … a different thread owns it`.

# Native GUI checks

- A GUI check never takes the focus of the owner, who works on this Mac meanwhile. `OMBRA_TEST_NO_ACTIVATE`
  (`avalonia/src/Ombra.App/Platform/TestFocus.cs`) is on by default for every run a harness drives: the feature smoke,
  NightlyAcceptance, `--smoke-exit-after-ms` and benchmark runs, the performance gate, what Ombra.Tests starts, the
  `tools/verify` and `tools/package` scripts. The app is then never activated (activation policy Prohibited, every
  activation call refused, no Dock bounce), its window opens behind the other apps' windows on the display left of the
  main one, its Nightly cannot raise itself, keys and clicks are NSEvents posted in-process, and a driver's `Activate()`
  (`TestFocus.Activate`) gives the window Avalonia's activation in-process.
- A driver never calls `activateIgnoringOtherApps:`, `activateWithOptions:`, `makeKeyAndOrderFront:`, `osascript …
  activate` or System Events keystrokes, and starts no `screencapture` without Screen Recording
  (`TestFocus.ScreenCaptureAllowed`): macOS asks for it in a window that takes the focus.
- Run a GUI check under `avalonia/tools/verify/front-app-watch.sh <log> <command…>` (feature-smoke/run.sh does): it
  records the front app every 100 ms and fails when the check's own processes became the front app. Only a check of
  app activation itself runs with `OMBRA_TEST_NO_ACTIVATE=0` (NightlyAcceptance `restore`, macos-gate's restore).
