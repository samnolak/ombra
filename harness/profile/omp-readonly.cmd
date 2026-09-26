@echo off
rem Technically read-only omp session on the "harness" profile (no write/edit/bash/task): research and planning only.
rem Runs the omp on PATH.
set OMP_PROFILE=harness
omp --profile harness --tools read,grep,glob,web_search %*
