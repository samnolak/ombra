@echo off
rem Technically read-only omp session (no write/edit/bash/task): research and planning only.
rem Runs the omp on PATH with your normal agent dir; the harness applies when OMBRA_HARNESS_DIR points at it
rem (Ombra sets it for the omp it launches).
omp --tools read,grep,glob,web_search %*
