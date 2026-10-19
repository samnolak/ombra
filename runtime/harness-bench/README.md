# Harness bench

Compares two engine trees (baseline vs changed harness) on fixed tasks with the same model. Results and conclusions:
K5 in `docs/PRODUCT_DIRECTIONS.md`. Every script documents its arguments in its header comment.

- `run.mjs` + `model.mjs` + `tasks.mjs` + `compare.mjs` — scripted OpenAI-compatible model with fault injection
  (429, 503, stream drop, truncated tool JSON, small server window); `BENCH_MODEL=` runs the same tasks on a real model.
- `polyglot.mjs` — Aider Polyglot subset (`polyglot-subset.json`), no Docker.
- `swebench.mjs` — SWE-bench Verified subset (`swebench-subset.json`, 20 instances), scored by the official
  `swebench` harness in Docker with Epoch AI's arm64 images (native under colima on Apple silicon).

## SWE-bench

One-time setup (`SWEBENCH_DATA`, default `.temp/SweBench`): a Python ≥ 3.10 venv with `swebench` and `datasets` and
`verified.jsonl` (`load_dataset("SWE-bench/SWE-bench_Verified", split="test").to_json(...)`). Images come from
`docker pull`; behind a TLS-intercepting proxy colima's Docker cannot verify ghcr.io, and the script falls back to
the `crane` binary (go-containerregistry) on the host plus `docker load`. Keep `NODE_EXTRA_CA_CERTS` set for the
agent's provider requests when such a proxy is on.

```sh
BENCH_BUDGET_USD=10 bun swebench.mjs run <out> <installed-runtime> gold=gold \
  haiku-base=<baseline-engine>=anthropic/claude-haiku-4-5=low haiku-k5=runtime/engine=anthropic/claude-haiku-4-5=low
bun swebench.mjs report <out> results/swebench-<date>.json
```

The `gold` side applies the reference patch: an instance it does not resolve has a broken environment on this
machine and goes to `excluded` in the subset. The sides of one instance run concurrently, so every side sees the
same machine load. The agent works offline (`swebench-offline.yml`: no web search, URL reads or browser).
`python`/`pytest`/`pip` on its PATH run in the instance's container. Completed runs and evaluations are cached in
`<out>`.

Caveats:
- `BENCH_BUDGET_USD` is checked only before an instance starts. Set it below the real cap by the cost of the most
  expensive instance (one haiku base+k5 pair cost up to ≈ $1.9) and use `BENCH_PARALLEL=1` for the paid sides.
- Some images ship a testbed that is already modified (sphinx `setup.py`/`tox.ini`, astropy `pyproject.toml`,
  requests `build/`). Those files land in every side's patch, gold included, and inflate `linesAdded`/`extraEdits`.
  `results/swebench-2026-10-19.json` gives corrected values that exclude the gold side's non-gold files.
