# Temporary files

- Screenshots, recordings and scratch images go to `.temp/` in this repo (gitignored), never the system temp folder.
  omp's browser screenshots land in `.temp/screenshots` (`browser.screenshotDir` in the local `.omp/config.yml`);
  for test screenshots set `OMBRA_SHOT_DIR` / `OMBRA_REVIEW_DIR` to a folder under `.temp/`.
- Before finishing a task, delete what you put in `.temp/`, unless the user asked to keep it (then say where it is).
- Tests clean up after themselves: a run writes every temp file under `<temp>/ombra-tests/run-<pid>`, deleted on exit,
  and the next run deletes folders of runs that were killed (`avalonia/tests/Ombra.Tests/TestEnvironment.cs`). Tests
  create temp folders with `TestProcesses.TempDir`, not the system temp folder directly.
