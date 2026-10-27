// SWE-bench Verified subset on real models: several "sides" (engine tree × model) on the fixed instances of
// swebench-subset.json (20) or, with BENCH_SUBSET=swebench-subset-50.json, its 50-instance superset, scored by the
// official harness (`swebench.harness.run_evaluation`) in Docker.
//
//   bun swebench.mjs select [20|50]                           # (re)writes swebench-subset.json / swebench-subset-50.json
//   bun swebench.mjs prefetch                                 # image tars of the subset into <data>/images (crane)
//   bun swebench.mjs run <out-dir> <runtime-dir> <side>...    # agent runs + official evaluation
//   bun swebench.mjs report <out-dir> <results.json>          # per-side table (+ JSON)
//
//   <side> = <name>=<engine-dir>=<provider/model>[=<thinking>[=shipped|=shipped:<harness-dir>]]   e.g. haiku-k5=runtime/engine=anthropic/claude-haiku-4-5=low
//            <name>=gold                                          applies the gold patch (image/pipeline validation)
//            <name>=codex=<model>[=<effort>]                      OpenAI Codex CLI (`codex exec`, see codex.mjs), e.g. codex-luna=codex=gpt-6-luna=low
//            <name>=claude=<model>[=<effort>]                     Anthropic Claude Code (`claude -p`, see claudecode.mjs), e.g. cc=claude=claude-haiku-5-5=low
//            `shipped`: extensions, skills and rules stay on and OMBRA_HARNESS_DIR is the repository's harness/profile;
//            `shipped:<harness-dir>`: the same with OMBRA_HARNESS_DIR=<harness-dir> (a variant, e.g. .temp/lab/variants/x)
//
// Environment:
//   SWEBENCH_DATA  folder with verified.jsonl (`SWE-bench/SWE-bench_Verified`, test split, as JSON lines), the image
//                  tar cache and the Python venv with `swebench` (SWEBENCH_PY, default <data>/venv/bin/python)
//   CRANE          go-containerregistry `crane` (default <data>/crane): images are pulled on the host into the tar
//                  cache <data>/images/<id>.tar and `docker load`ed (behind a TLS-intercepting proxy colima's VM does
//                  not trust the proxy's CA while macOS does); `docker pull` is the last resort, cut after
//                  BENCH_DOCKER_PULL_TIMEOUT_MS (default 120 s)
//   DOCKER_HOST    default unix://$HOME/.colima/default/docker.sock
//   BENCH_TIMEOUT_MS  per agent run (default 15 min); BENCH_BUDGET_USD  stop starting runs once the summed cost of
//                  every side in <out-dir> reaches it; BENCH_ONLY  comma-separated instance ids; BENCH_SUBSET  subset
//                  file; BENCH_PARALLEL  instances at a time (default 1); BENCH_KEEP_IMAGES=1  no `docker rmi` after
//                  an instance; codex sides: BENCH_CODEX_BIN, BENCH_CODEX_HOME, BENCH_NODE (codex.mjs); claude sides:
//                  BENCH_CLAUDE_BIN (claudecode.mjs)
//
// Images: Epoch AI's arm64 registry (ghcr.io/epoch-research/swe-bench.eval.arm64.<id>), which runs natively under
// colima on Apple silicon. `select` takes only instances with a published arm64 image; instances whose image does not
// resolve under the gold patch are listed in the subset's `excluded` and replaced from the same stratum.
//
// Per run the instance image's /testbed is copied to a host folder under ~/.cache (colima mounts $HOME) and a
// container of the same image is started with that folder mounted as /testbed. The agent (engine CLI, `-p`, yolo,
// isolated HOME, `--no-session`, extensions/skills/rules/LSP off; or `codex exec` / `claude -p` with full access, see
// codex.mjs and claudecode.mjs)
// edits the host copy; `python`, `python3`, `pytest`, `pip` on its PATH are wrappers that run in that container with
// host paths mapped to /testbed, so the agent can reproduce the issue and run tests in the instance's real
// environment. The patch is `git diff` of the copy (new files included, binary files dropped) and goes to the official
// harness unchanged.
// Columns (owner's criteria): resolved, wall time, lines +/− and files changed, edits outside the gold patch's files,
// model requests, failed tool calls, test runs, input/output tokens and cost. Credentials: the user's agent dir is read
// the way any second omp process reads it; nothing here prints or copies them. The user's own MCP servers, extensions,
// rules, skills and agents in that dir stay off on every engine side (isolation.mjs: a `--config` overlay written to
// <out-dir>/<side>/isolation.yml, after swebench-offline.yml), as a Codex or Claude Code side gets a fresh CODEX_HOME /
// CLAUDE_CONFIG_DIR.
// Cache: a run whose result.json exists is not repeated; an evaluation whose report.json exists is not repeated.
import { spawn, spawnSync } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { claudeMetrics, runClaude } from "./claudecode.mjs";
import { codexMetrics, runCodex } from "./codex.mjs";
import { userIsolationArgs } from "./isolation.mjs";

const HERE = path.dirname(new URL(import.meta.url).pathname);
const SUBSET = path.resolve(process.env.BENCH_SUBSET ?? path.join(HERE, "swebench-subset.json"));
const DATA = path.resolve(process.env.SWEBENCH_DATA ?? path.join(HERE, "../../.temp/SweBench"));
const PY = process.env.SWEBENCH_PY ?? path.join(DATA, "venv/bin/python");
const CRANE = process.env.CRANE ?? path.join(DATA, "crane");
const DOCKER_HOST = process.env.DOCKER_HOST ?? `unix://${os.homedir()}/.colima/default/docker.sock`;
const DOCKER = ["/opt/homebrew/bin/docker", "/usr/local/bin/docker"].find(p => fs.existsSync(p)) ?? "docker";
const TIMEOUT_MS = Number(process.env.BENCH_TIMEOUT_MS ?? 15 * 60 * 1000);
const AGENT_DIR = process.env.BENCH_AGENT_DIR ?? path.join(os.homedir(), ".omp/agent");
const OFFLINE_CONFIG = path.join(HERE, "swebench-offline.yml");
const IMAGE = id => `ghcr.io/epoch-research/swe-bench.eval.arm64.${id}:latest`;
// The harness stops its containers with the `docker` CLI; without it on PATH they are left running.
const dockerEnv = { ...process.env, DOCKER_HOST, PATH: `${path.dirname(DOCKER)}:${process.env.PATH}` };

const sh = (cmd, args, opts = {}) => spawnSync(cmd, args, { encoding: "utf8", maxBuffer: 256 << 20, env: dockerEnv, ...opts });
const docker = (...args) => sh(DOCKER, args);
const git = (cwd, ...args) => sh("git", ["-c", "core.fileMode=false", ...args], { cwd });
const loadDataset = () => new Map(fs.readFileSync(path.join(DATA, "verified.jsonl"), "utf8").split("\n").filter(Boolean).map(l => JSON.parse(l)).map(r => [r.instance_id, r]));
const goldFiles = patch => [...new Set([...patch.matchAll(/^diff --git a\/(\S+) b\//gm)].map(m => m[1]))];

// ---- select ------------------------------------------------------------------------------------------------------
// Stratified by repository (≈ the Verified mix) and by the dataset's `difficulty` label. Within a stratum instances are
// ordered by sha256(instance_id), so the pick is fixed and reproducible; the first with an arm64 image that is not in
// `excluded` is taken. A larger size only raises stratum counts, so it is a superset of the smaller one (listed first).
const Q = "<15 min fix", H = "15 min - 1 hour", L = "1-4 hours";
const STRATA = {
	20: {
		"django/django": { [Q]: 2, [H]: 2, [L]: 2 },
		"sympy/sympy": { [Q]: 1, [H]: 1, [L]: 1 },
		"sphinx-doc/sphinx": { [Q]: 1, [H]: 1 },
		"matplotlib/matplotlib": { [Q]: 1, [H]: 1 },
		"scikit-learn/scikit-learn": { [Q]: 1, [H]: 1 },
		"astropy/astropy": { [H]: 1 },
		// pydata/xarray has no arm64 image at all (0 of 22): its slot went to mwaskom/seaborn, and the 1–4 h slot to django.
		"mwaskom/seaborn": { [H]: 1 },
		"pytest-dev/pytest": { [Q]: 1 },
		"pylint-dev/pylint": { [H]: 1 },
		"psf/requests": { [Q]: 1 },
	},
	// Verified without xarray, scaled to 50: django 18, sympy 8, sphinx 5, matplotlib 4, scikit-learn 4, astropy 3,
	// pytest 3, pylint 2, requests 2, seaborn 1; difficulty 19 × <15 min, 26 × 15 min–1 h, 5 × 1–4 h (≈ 39/52/9 %).
	50: {
		"django/django": { [Q]: 7, [H]: 8, [L]: 3 },
		"sympy/sympy": { [Q]: 3, [H]: 4, [L]: 1 },
		"sphinx-doc/sphinx": { [Q]: 2, [H]: 3 },
		"matplotlib/matplotlib": { [Q]: 2, [H]: 2 },
		"scikit-learn/scikit-learn": { [Q]: 1, [H]: 2, [L]: 1 },
		"astropy/astropy": { [Q]: 1, [H]: 2 },
		"mwaskom/seaborn": { [H]: 1 },
		"pytest-dev/pytest": { [Q]: 1, [H]: 2 },
		"pylint-dev/pylint": { [Q]: 1, [H]: 1 },
		"psf/requests": { [Q]: 1, [H]: 1 },
	},
};
const subsetFile = size => path.join(HERE, size === 20 ? "swebench-subset.json" : `swebench-subset-${size}.json`);

// `select [size]` writes BENCH_SUBSET, default swebench-subset.json (20) or swebench-subset-<size>.json.
function select(size) {
	const strata = STRATA[size];
	if (!strata) throw new Error(`no strata for size ${size} (have ${Object.keys(STRATA).join(", ")})`);
	const target = process.env.BENCH_SUBSET ? SUBSET : subsetFile(size);
	const read = f => (fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, "utf8")) : {});
	const base = size === 20 ? {} : read(subsetFile(20));
	const excluded = { ...base.excluded, ...read(target).excluded };
	const rows = [...loadDataset().values()];
	const hash = id => crypto.createHash("sha256").update(id).digest("hex");
	const chosen = [];
	for (const [repo, want] of Object.entries(strata)) {
		for (const [level, n] of Object.entries(want)) {
			const pool = rows.filter(r => r.repo === repo && r.difficulty === level).sort((a, b) => hash(a.instance_id).localeCompare(hash(b.instance_id)));
			let got = 0;
			for (const r of pool) {
				if (got === n) break;
				if (excluded[r.instance_id]) continue;
				if (sh(CRANE, ["digest", "--platform", "linux/arm64", IMAGE(r.instance_id)]).status !== 0) {
					excluded[r.instance_id] = "no arm64 image in the Epoch AI registry";
					continue;
				}
				chosen.push({ instance_id: r.instance_id, repo, difficulty: level });
				got++;
			}
			if (got < n) console.error(`stratum ${repo} / ${level}: only ${got} of ${n}`);
		}
	}
	const baseIds = (base.instances ?? []).map(i => i.instance_id);
	const rank = id => (baseIds.includes(id) ? baseIds.indexOf(id) : baseIds.length);
	chosen.sort((a, b) => rank(a.instance_id) - rank(b.instance_id));
	const count = f => Object.entries(Object.groupBy(chosen, f)).map(([k, v]) => `${k} ${v.length}`).join(", ");
	const out = {
		dataset: "SWE-bench/SWE-bench_Verified (test split, 500 instances)",
		images: "ghcr.io/epoch-research/swe-bench.eval.arm64.<instance_id> (native arm64 under colima)",
		selection: `stratified: repositories ≈ the Verified mix (${count(c => c.repo)}; xarray has no arm64 image), difficulty ${count(c => c.difficulty)}; within a stratum by sha256(instance_id), skipping \`excluded\`${baseIds.length ? `; the ${baseIds.length} instances of swebench-subset.json first` : ""}`,
		instances: chosen,
		excluded,
	};
	fs.writeFileSync(target, JSON.stringify(out, null, 2) + "\n");
	console.log(chosen.map(c => `${c.instance_id}\t${c.difficulty}`).join("\n"));
}

// Long docker/crane steps run asynchronously: a blocked event loop would stall the agents of the other sides.
// `timeoutMs` kills the step (SIGKILL) and resolves with `timedOut: true`.
const shAsync = (cmd, args, { timeoutMs, ...opts } = {}) => new Promise(resolve => {
	const p = spawn(cmd, args, { env: dockerEnv, stdio: ["ignore", "pipe", "pipe"], ...opts });
	let output = "", timedOut = false;
	const timer = timeoutMs && setTimeout(() => { timedOut = true; p.kill("SIGKILL"); }, timeoutMs);
	p.stdout.on("data", d => (output += d));
	p.stderr.on("data", d => (output += d));
	p.on("close", status => { clearTimeout(timer); resolve({ status, output, timedOut }); });
});

// The host pulls the image with crane into the tar cache <data>/images/<id>.tar (kept across runs; colima's Docker
// does not trust a TLS-intercepting proxy's CA, see CRANE above). 3 attempts of at most 30 min each. Layers go through
// crane's layer cache <data>/images/layers2: the instance images share their large base layers, and a failed attempt
// keeps the layers it finished.
const LAYER_CACHE = path.join(DATA, "images", "layers2");
async function fetchImage(id) {
	const tar = path.join(DATA, "images", `${id}.tar`);
	if (fs.existsSync(tar)) return tar;
	fs.mkdirSync(path.dirname(tar), { recursive: true });
	const part = `${tar}.part.${process.pid}`;
	for (let attempt = 1; attempt <= 3; attempt++) {
		const p = await shAsync(CRANE, ["pull", "--platform", "linux/arm64", "--format", "legacy", "--cache_path", LAYER_CACHE, IMAGE(id), part], { timeoutMs: 30 * 60 * 1000 });
		if (p.status === 0) { fs.renameSync(part, tar); return tar; }
		fs.rmSync(part, { force: true });
		console.error(`crane pull ${id}: attempt ${attempt} ${p.timedOut ? "timed out" : "failed"}`);
	}
	throw new Error(`crane pull ${id} failed`);
}

// Image in colima's Docker: `docker load` of the tar cache (crane-pulled first when missing); `docker pull` only as the
// last resort, cut at BENCH_DOCKER_PULL_TIMEOUT_MS (default 120 s): behind the proxy it can hang for hours.
const DOCKER_PULL_TIMEOUT_MS = Number(process.env.BENCH_DOCKER_PULL_TIMEOUT_MS ?? 120000);
async function ensureImage(id) {
	if (docker("image", "inspect", IMAGE(id)).status === 0) return;
	let why;
	try {
		const tar = await fetchImage(id);
		const l = await shAsync(DOCKER, ["load", "-i", tar]);
		if (l.status === 0 && docker("image", "inspect", IMAGE(id)).status === 0) return;
		fs.rmSync(tar, { force: true });
		why = `docker load: ${l.output.trim()}`;
	} catch (e) { why = e.message; }
	console.error(`${id}: ${why}; trying docker pull`);
	const p = await shAsync(DOCKER, ["pull", "-q", IMAGE(id)], { timeoutMs: DOCKER_PULL_TIMEOUT_MS });
	if (p.status === 0 && docker("image", "inspect", IMAGE(id)).status === 0) return;
	throw new Error(`${why}; docker pull ${p.timedOut ? `timed out after ${DOCKER_PULL_TIMEOUT_MS / 1000} s` : `failed: ${p.output.trim()}`}`);
}

// ---- run -----------------------------------------------------------------------------------------------------------
function parseSide(s) {
	const [name, engine, model, thinking, flag] = s.split("=");
	if (engine === "gold") return { name, gold: true };
	if (engine === "codex") return { name, codex: true, codexModel: model, model: `codex/${model}`, thinking };
	if (engine === "claude") return { name, claude: true, claudeModel: model, model: `claude-code/${model}`, thinking };
	if (flag !== undefined && flag !== "shipped" && !flag.startsWith("shipped:")) throw new Error(`unknown side flag "${flag}" in ${s}`);
	const harnessDir = flag === undefined ? undefined : path.resolve(flag === "shipped" ? path.join(HERE, "../../harness/profile") : flag.slice("shipped:".length));
	if (harnessDir && !fs.existsSync(harnessDir)) throw new Error(`harness dir ${harnessDir} does not exist (${s})`);
	return { name, engine: path.resolve(engine), model, thinking, shipped: Boolean(harnessDir), harnessDir };
}

function prepareEngine(side, rt, out) {
	const omp = path.join(out, side.name, "rt/omp");
	fs.mkdirSync(omp, { recursive: true });
	sh("rsync", ["-a", "--delete", "--exclude", "node_modules", "--exclude", ".temp", side.engine + "/", path.join(omp, "engine") + "/"], { stdio: "inherit" });
	for (const f of ["bunfig.toml", "package.json"]) fs.copyFileSync(path.join(rt, "omp", f), path.join(omp, f));
	if (!fs.existsSync(path.join(omp, "node_modules"))) fs.symlinkSync(path.join(rt, "omp/node_modules"), path.join(omp, "node_modules"));
	side.omp = omp;
	side.home = path.join(out, side.name, "home");
	fs.mkdirSync(side.home, { recursive: true });
	side.isolationArgs = userIsolationArgs({ agentDir: AGENT_DIR, harnessDir: side.harnessDir, configs: [OFFLINE_CONFIG], outFile: path.join(out, side.name, "isolation.yml") });
}

const WRAPPED = ["python", "python3", "pytest", "py.test", "pip", "pip3"];
function writeWrappers(bin, work, container) {
	fs.mkdirSync(bin, { recursive: true });
	for (const name of WRAPPED) {
		const f = path.join(bin, name);
		fs.writeFileSync(f, `#!/bin/bash
# ${name} in the task's container, where this checkout (${work}) is /testbed.
W='${work}'
d=/testbed; case "$PWD/" in "$W"/*) d="/testbed\${PWD#"$W"}";; esac
args=(); for a in "$@"; do args+=("\${a//"$W"//testbed}"); done
exec '${DOCKER}' exec -i -w "$d" '${container}' bash -c 'source /opt/miniconda3/bin/activate testbed && exec "$@"' bash ${name} \${args[@]+"\${args[@]}"}
`);
		fs.chmodSync(f, 0o755);
	}
}

const prompt = inst => `<issue>
${inst.problem_statement.trim()}
</issue>

The ${inst.repo} repository is checked out in the current directory at the commit where this issue was reported. Make the minimal changes to non-test source files so that the issue is resolved; the changes to test files are already taken care of, so do not modify tests. \`python\`, \`python3\`, \`pytest\` and \`pip\` here run inside the project's prepared environment (a container where this directory is /testbed): use them to reproduce the issue and to run the existing tests. Work offline: do not look up this issue or its fix on the internet.`;

function runAgent(side, work, bin, inst) {
	const BUN = path.join(side.rt, "bun/bun");
	const args = ["--config=" + path.join(side.omp, "bunfig.toml"), "--no-env-file", "--no-install", path.join(side.omp, "engine/coding-agent/src/cli.ts"),
		"-p", "--mode", "json", "--approval-mode", "yolo", "--no-session", "--model", side.model, ...(side.thinking ? ["--thinking", side.thinking] : []),
		...(side.shipped ? [] : ["--no-extensions", "--no-skills", "--no-rules"]), "--no-lsp", "--no-prewalk", "--no-title", "--config", OFFLINE_CONFIG, ...side.isolationArgs, prompt(inst)];
	return new Promise(resolve => {
		const t0 = Date.now();
		const p = spawn("nice", ["-n", "10", BUN, ...args], {
			cwd: work, detached: true,
			// NODE_EXTRA_CA_CERTS: behind a TLS-intercepting proxy the provider requests fail with "Connection error." without it.
			env: { PATH: `${bin}:${path.dirname(BUN)}:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin`, HOME: side.home, PI_CODING_AGENT_DIR: AGENT_DIR, TERM: "dumb", DOCKER_HOST, ...(side.shipped ? { OMBRA_HARNESS_DIR: side.harnessDir } : {}), ...(process.env.NODE_EXTRA_CA_CERTS ? { NODE_EXTRA_CA_CERTS: process.env.NODE_EXTRA_CA_CERTS } : {}) },
			stdio: ["ignore", "pipe", "pipe"],
		});
		let stdout = "", stderr = "", timedOut = false;
		p.stdout.on("data", d => (stdout += d));
		p.stderr.on("data", d => (stderr += d));
		const timer = setTimeout(() => { timedOut = true; try { process.kill(-p.pid, "SIGKILL"); } catch {} }, TIMEOUT_MS);
		p.on("close", code => { clearTimeout(timer); resolve({ stdout, stderr, code, timedOut, wallMs: Date.now() - t0 }); });
	});
}

// The copy's changes as a patch the harness can `git apply`: tracked edits and new text files; binary or >1 MB new
// files (build output the agent left behind) are dropped.
function extractPatch(work) {
	git(work, "add", "-A");
	const num = git(work, "diff", "--cached", "--numstat").stdout.trim().split("\n").filter(Boolean).map(l => l.split("\t"));
	const drop = num.filter(([a, , f]) => a === "-" || (fs.existsSync(path.join(work, f)) && fs.statSync(path.join(work, f)).size > 1 << 20)).map(([, , f]) => f);
	if (drop.length) git(work, "reset", "-q", "--", ...drop);
	const patch = git(work, "diff", "--cached").stdout;
	const stat = num.filter(([, , f]) => !drop.includes(f)).map(([a, r, f]) => ({ file: f, added: Number(a), removed: Number(r) }));
	const created = git(work, "diff", "--cached", "--name-only", "--diff-filter=A").stdout.trim().split("\n").filter(Boolean);
	return { patch, stat, created, dropped: drop };
}

function eventsOf(stdout) {
	return stdout.split("\n").filter(l => l.startsWith("{")).map(l => { try { return JSON.parse(l); } catch { return undefined; } }).filter(Boolean);
}

const TEST_RE = /\b(pytest|py\.test|runtests\.py|bin\/test|unittest|tox)\b/;
function engineMetrics(stdout) {
	const events = eventsOf(stdout);
	const assistant = events.filter(e => e.type === "message_end" && e.message?.role === "assistant");
	const starts = events.filter(e => e.type === "tool_execution_start");
	const ends = events.filter(e => e.type === "tool_execution_end");
	const usage = assistant.reduce((u, e) => {
		const x = e.message.usage ?? {};
		return { input: u.input + (x.input ?? 0) + (x.cacheRead ?? 0) + (x.cacheWrite ?? 0), cacheRead: u.cacheRead + (x.cacheRead ?? 0), output: u.output + (x.output ?? 0), cost: u.cost + (x.cost?.total ?? 0) };
	}, { input: 0, cacheRead: 0, output: 0, cost: 0 });
	return {
		requests: assistant.length,
		toolCalls: starts.length,
		toolErrors: ends.filter(e => e.isError).length,
		testRuns: starts.filter(e => e.toolName === "bash" && TEST_RE.test(String(e.args?.command ?? ""))).length,
		// K5 attribution: reads answered with a structural outline (read.summarize) and critical-command refusals.
		readOutlines: ends.filter(e => e.toolName === "read" && e.result?.details?.summary).length,
		criticalBlocks: ends.filter(e => e.isError && /Critical pattern/i.test(JSON.stringify(e.result ?? ""))).length,
		inputTokens: usage.input, cacheReadTokens: usage.cacheRead, outputTokens: usage.output, costUsd: Number(usage.cost.toFixed(4)),
		stopReason: assistant.at(-1)?.message?.stopReason, error: assistant.at(-1)?.message?.errorMessage,
	};
}

async function runOne(side, inst, out) {
	const dir = path.join(out, side.name, "runs", inst.instance_id);
	const cached = path.join(dir, "result.json");
	if (fs.existsSync(cached)) return JSON.parse(fs.readFileSync(cached, "utf8"));
	fs.rmSync(dir, { recursive: true, force: true });
	fs.mkdirSync(dir, { recursive: true });
	// Under $HOME (colima mounts it) and outside any repository with AGENTS.md.
	const base = path.join(os.homedir(), ".cache/harness-swebench", path.basename(out), side.name, inst.instance_id);
	const work = path.join(base, "testbed"), bin = path.join(base, "bin");
	const container = `swb-${path.basename(out)}-${side.name}-${inst.instance_id}`.toLowerCase().replace(/[^a-z0-9_.-]/g, "-");
	docker("rm", "-f", container);
	fs.rmSync(base, { recursive: true, force: true });
	fs.mkdirSync(base, { recursive: true });
	const tmp = docker("create", IMAGE(inst.instance_id)).stdout.trim();
	const cp = await shAsync(DOCKER, ["cp", `${tmp}:/testbed`, work]);
	docker("rm", "-f", tmp);
	if (cp.status !== 0) throw new Error(`docker cp ${inst.instance_id}: ${cp.output}`);
	// A checkout that is already dirty on the host (case-insensitive file system, symlinks) would leak into the patch.
	const dirty = git(work, "status", "--porcelain").stdout.trim();
	let row = { instance_id: inst.instance_id, side: side.name, model: side.model ?? "gold", difficulty: inst.difficulty, setupDirty: dirty ? dirty.split("\n").length : 0 };
	if (side.gold) {
		fs.writeFileSync(path.join(dir, "gold.diff"), inst.patch);
		const a = git(work, "apply", path.join(dir, "gold.diff"));
		row.wallMs = 0;
		if (a.status !== 0) row.applyError = a.stderr;
	} else {
		const run = docker("run", "-d", "--name", container, "-v", `${work}:/testbed`, IMAGE(inst.instance_id), "tail", "-f", "/dev/null");
		if (run.status !== 0) throw new Error(`docker run ${inst.instance_id}: ${run.stderr}`);
		writeWrappers(bin, work, container);
		const cli = { effort: side.thinking, prompt: prompt(inst), cwd: work, home: side.home, pathPrefix: bin, timeoutMs: TIMEOUT_MS, env: { DOCKER_HOST } };
		const r = side.codex ? await runCodex({ model: side.codexModel, ...cli })
			: side.claude ? await runClaude({ model: side.claudeModel, ...cli })
			: await runAgent(side, work, bin, inst);
		docker("rm", "-f", container);
		fs.writeFileSync(path.join(dir, "stdout.jsonl"), r.stdout);
		fs.writeFileSync(path.join(dir, "stderr.txt"), r.stderr);
		if (side.codex) fs.writeFileSync(path.join(dir, "rollout.jsonl"), r.rollout);
		row = { ...row, wallMs: r.wallMs, exitCode: r.code, timedOut: r.timedOut, ...(side.codex ? codexMetrics(r, TEST_RE) : side.claude ? claudeMetrics(r, TEST_RE) : engineMetrics(r.stdout)) };
	}
	const { patch, stat, created, dropped } = extractPatch(work);
	fs.writeFileSync(path.join(dir, "model.patch"), patch);
	const gold = goldFiles(inst.patch);
	const files = stat.map(s => s.file);
	row = {
		...row,
		filesChanged: files.length,
		linesAdded: stat.reduce((n, s) => n + s.added, 0),
		linesRemoved: stat.reduce((n, s) => n + s.removed, 0),
		changedFiles: files,
		extraEdits: files.filter(f => !gold.includes(f)).length,
		extraTrackedEdits: files.filter(f => !gold.includes(f) && !created.includes(f)).length,
		droppedFiles: dropped,
		patchBytes: Buffer.byteLength(patch),
	};
	fs.rmSync(base, { recursive: true, force: true });
	fs.writeFileSync(cached, JSON.stringify(row, null, 2));
	return row;
}

// Official harness, one instance and side per call; the dataset copy points `image` at the arm64 image.
async function evaluate(side, inst, out) {
	const dir = path.join(out, side.name, "runs", inst.instance_id);
	const evalDir = path.join(out, "eval");
	const report = path.join(evalDir, "logs/run_evaluation", side.name, side.name, inst.instance_id, "report.json");
	if (!fs.existsSync(report)) {
		const patch = fs.readFileSync(path.join(dir, "model.patch"), "utf8");
		if (!patch.trim()) return { resolved: false, evalStatus: "empty patch" };
		fs.mkdirSync(evalDir, { recursive: true });
		const ds = path.join(evalDir, `${inst.instance_id}.dataset.jsonl`);
		fs.writeFileSync(ds, JSON.stringify({ ...inst, image: IMAGE(inst.instance_id) }) + "\n");
		const preds = path.join(dir, "preds.jsonl");
		fs.writeFileSync(preds, JSON.stringify({ instance_id: inst.instance_id, model_name_or_path: side.name, model_patch: patch }) + "\n");
		const p = await shAsync("nice", ["-n", "10", PY, "-m", "swebench.harness.run_evaluation", "-d", ds, "-p", preds, "-id", side.name, "-i", inst.instance_id, "--max_workers", "1", "-t", "900", "--report_dir", path.join(evalDir, "reports")], { cwd: evalDir });
		fs.writeFileSync(path.join(dir, "eval.log"), p.output);
		if (!fs.existsSync(report)) return { resolved: false, evalStatus: "error (see eval.log)" };
	}
	const r = JSON.parse(fs.readFileSync(report, "utf8"))[inst.instance_id] ?? {};
	return { resolved: r.resolved === true, evalStatus: r.patch_successfully_applied === false ? "patch did not apply" : "evaluated" };
}

// BENCH_BUDGET_USD caps Anthropic spend (engine anthropic/ and Claude Code sides; Claude Code's cost is the API
// equivalent of the same Anthropic login); subscription providers (openai-codex) report token counts only.
const ANTHROPIC = model => model?.startsWith("anthropic/") || model?.startsWith("claude-code/");
const costSoFar = out => fs.readdirSync(out, { withFileTypes: true }).filter(d => d.isDirectory() && fs.existsSync(path.join(out, d.name, "runs")))
	.flatMap(d => fs.readdirSync(path.join(out, d.name, "runs")).map(i => path.join(out, d.name, "runs", i, "result.json")))
	.filter(f => fs.existsSync(f)).map(f => JSON.parse(fs.readFileSync(f, "utf8"))).filter(r => ANTHROPIC(r.model)).reduce((n, r) => n + (r.costUsd ?? 0), 0);

async function run(outArg, rtArg, sideArgs) {
	const out = path.resolve(outArg), rt = path.resolve(rtArg);
	const sides = sideArgs.map(parseSide);
	for (const s of sides) {
		if (s.codex || s.claude) { s.home = path.join(out, s.name, "home"); fs.mkdirSync(s.home, { recursive: true }); }
		else if (!s.gold) { s.rt = rt; prepareEngine(s, rt, out); }
	}
	const data = loadDataset();
	const subset = JSON.parse(fs.readFileSync(SUBSET, "utf8"));
	const only = process.env.BENCH_ONLY?.split(",");
	const budget = Number(process.env.BENCH_BUDGET_USD ?? Infinity);
	const queue = subset.instances.map(i => i.instance_id).filter(id => !only || only.includes(id));
	let stopped = false;
	// The sides of one instance run concurrently (same load on every side), each evaluated as soon as it finishes;
	// BENCH_PARALLEL instances at a time.
	const worker = async () => {
		for (let instance_id; !stopped && (instance_id = queue.shift()); ) {
			const inst = data.get(instance_id);
			const pending = sides.filter(s => !fs.existsSync(path.join(out, s.name, "runs", instance_id, "result.json")));
			if (pending.some(s => ANTHROPIC(s.model)) && costSoFar(out) >= budget) {
				console.error(`budget $${budget} reached; stopping before ${instance_id}`);
				stopped = true;
				break;
			}
			try { await ensureImage(instance_id); } catch (e) { console.error(`${instance_id}: ${e.message}`); continue; }
			await Promise.all(sides.map(async s => {
				let row;
				try { row = await runOne(s, inst, out); } catch (e) { console.error(`${s.name} ${instance_id}: ${e.message ?? e}`); return; }
				Object.assign(row, await evaluate(s, inst, out));
				fs.writeFileSync(path.join(out, s.name, "runs", instance_id, "result.json"), JSON.stringify(row, null, 2));
				console.log(JSON.stringify({ side: s.name, instance_id, resolved: row.resolved, wallS: Math.round(row.wallMs / 1000), costUsd: row.costUsd, evalStatus: row.evalStatus }));
			}));
			if (process.env.BENCH_KEEP_IMAGES !== "1") docker("rmi", IMAGE(instance_id));
		}
	};
	await Promise.all(Array.from({ length: Number(process.env.BENCH_PARALLEL ?? 1) }, worker));
}

// ---- report --------------------------------------------------------------------------------------------------------
function report(outArg, resultsArg) {
	const out = path.resolve(outArg);
	const subset = JSON.parse(fs.readFileSync(SUBSET, "utf8"));
	const ids = subset.instances.map(i => i.instance_id);
	const sides = fs.readdirSync(out, { withFileTypes: true }).filter(d => d.isDirectory() && fs.existsSync(path.join(out, d.name, "runs"))).map(d => d.name);
	const median = xs => { const s = [...xs].sort((a, b) => a - b); return s.length ? (s.length % 2 ? s[(s.length - 1) / 2] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2) : 0; };
	const summary = {};
	const runs = {};
	for (const side of sides) {
		const rows = ids.map(id => path.join(out, side, "runs", id, "result.json")).filter(f => fs.existsSync(f)).map(f => JSON.parse(fs.readFileSync(f, "utf8")));
		runs[side] = rows;
		const sum = k => rows.reduce((n, r) => n + (r[k] ?? 0), 0);
		const mean = k => (rows.length ? sum(k) / rows.length : 0);
		summary[side] = {
			model: rows[0]?.model, runs: rows.length, resolved: rows.filter(r => r.resolved).length,
			wallMeanS: +(mean("wallMs") / 1000).toFixed(1), wallMedianS: +(median(rows.map(r => r.wallMs)) / 1000).toFixed(1), timedOut: rows.filter(r => r.timedOut).length,
			linesAddedMean: +mean("linesAdded").toFixed(1), linesRemovedMean: +mean("linesRemoved").toFixed(1), filesChangedMean: +mean("filesChanged").toFixed(2),
			extraEdits: sum("extraEdits"), extraTrackedEdits: sum("extraTrackedEdits"),
			requestsMean: +mean("requests").toFixed(1), toolErrorsMean: +mean("toolErrors").toFixed(2), testRunsMean: +mean("testRuns").toFixed(1),
			readOutlines: sum("readOutlines"), criticalBlocks: sum("criticalBlocks"),
			inputTokens: sum("inputTokens"), cacheReadTokens: sum("cacheReadTokens"), outputTokens: sum("outputTokens"), costUsd: +sum("costUsd").toFixed(2),
			resolvedIds: rows.filter(r => r.resolved).map(r => r.instance_id),
		};
	}
	fs.writeFileSync(path.resolve(resultsArg), JSON.stringify({ date: new Date().toISOString().slice(0, 10), subset: ids, summary, runs }, null, 2) + "\n");
	const table = [
		"| side | model | resolved | wall s mean / median | lines +/− (mean) | files (mean) | extra edits (all / tracked) | requests | tool errors | test runs | input tok | output tok | $ |",
		"|---|---|---|---|---|---|---|---|---|---|---|---|---|",
		...sides.map(s => { const x = summary[s]; return `| ${s} | ${x.model} | ${x.resolved}/${x.runs} | ${x.wallMeanS} / ${x.wallMedianS} | +${x.linesAddedMean}/−${x.linesRemovedMean} | ${x.filesChangedMean} | ${x.extraEdits} / ${x.extraTrackedEdits} | ${x.requestsMean} | ${x.toolErrorsMean} | ${x.testRunsMean} | ${x.inputTokens} | ${x.outputTokens} | ${x.costUsd} |`; }),
	].join("\n");
	console.log(table);
}

// ---- prefetch ------------------------------------------------------------------------------------------------------
// Image tars for the subset (BENCH_ONLY narrows it) into the tar cache <data>/images through fetchImage (crane);
// Docker is not touched. `run` then only `docker load`s them, so repeats and variants never hit the network.
// BENCH_PREFETCH_PARALLEL tars at a time (default 1).
async function prefetch() {
	const only = process.env.BENCH_ONLY?.split(",");
	const ids = JSON.parse(fs.readFileSync(SUBSET, "utf8")).instances.map(i => i.instance_id).filter(id => !only || only.includes(id));
	let failed = 0;
	const queue = [...ids];
	const worker = async () => {
		for (let id = queue.shift(); id; id = queue.shift()) {
			const t0 = Date.now();
			try { const tar = await fetchImage(id); console.log(`${id}\t${(fs.statSync(tar).size / 2 ** 30).toFixed(2)} GiB\t${Math.round((Date.now() - t0) / 1000)} s`); }
			catch (e) { failed++; console.error(`${id}: ${e.message}`); }
		}
	};
	await Promise.all(Array.from({ length: Number(process.env.BENCH_PREFETCH_PARALLEL ?? 1) }, worker));
	console.log(`prefetch: ${ids.length - failed}/${ids.length} tars in ${path.join(DATA, "images")}`);
}

const [cmd, ...rest] = process.argv.slice(2);
if (cmd === "select" && rest.length <= 1) select(Number(rest[0] ?? 20));
else if (cmd === "prefetch" && rest.length === 0) await prefetch();
else if (cmd === "run" && rest.length >= 3) await run(rest[0], rest[1], rest.slice(2));
else if (cmd === "report" && rest.length === 2) report(rest[0], rest[1]);
else {
	console.error("usage: bun swebench.mjs select [20|50] | prefetch | run <out-dir> <runtime-dir> <side>... | report <out-dir> <results.json>");
	process.exit(2);
}
