// Aider Polyglot subset on a real model: one engine tree, the fixed exercises of polyglot-subset.json, N runs each.
//
//   BENCH_MODEL=anthropic/claude-haiku-4-5 bun polyglot.mjs <engine-dir> <installed-runtime-dir> <polyglot-checkout> <out-dir> [runs=1]
//   BENCH_CODEX=1 BENCH_MODEL=gpt-6-luna BENCH_THINKING=low bun polyglot.mjs - <installed-runtime-dir> <polyglot-checkout> <out-dir> [runs=1]
//   BENCH_CLAUDE=1 BENCH_MODEL=claude-haiku-5-5 BENCH_THINKING=low bun polyglot.mjs - <installed-runtime-dir> <polyglot-checkout> <out-dir> [runs=1]
//
// BENCH_SUBSET: the exercise list, default polyglot-subset.json (10); a JSON file in the same shape (e.g. the
// polyglot-hard.json that select-hard.mjs writes), or `all` = every JavaScript and Python practice exercise of the
// checkout (83 at the pinned commit; the checkout must be at that commit).
//
// <polyglot-checkout>: `git clone https://github.com/Aider-AI/polyglot-benchmark` at the commit in polyglot-subset.json.
// Only JavaScript (tests run with `bun test`, Jest syntax) and Python (`python3 -m unittest`) exercises: no Docker or
// per-language toolchains. Every `xtest` is enabled, as Aider's harness does.
//
// Per exercise the agent gets the exercise instructions and the names of the solution and test files, may run the tests
// itself, and must not change the tests. Scoring restores the original test files first, then runs them.
// Columns (owner's criteria): pass, wall time, code written (lines added/removed and bytes in the solution files),
// unrequested edits (changed files other than the solution files, test files included), attempts (model requests,
// failed tool calls, test runs the agent made) and spend (provider input/output tokens and cost).
//
// Credentials: the user's existing agent dir (PI_CODING_AGENT_DIR, default ~/.omp/agent) is read the way any second
// omp process reads it; nothing here prints or copies them. The user's own MCP servers, extensions, rules, skills and
// agents in that dir stay off in every mode (isolation.mjs: a `--config` overlay written to <out-dir>/isolation.yml,
// after BENCH_CONFIG), as a Codex or Claude Code side gets a fresh CODEX_HOME / CLAUDE_CONFIG_DIR. HOME is a temp
// folder; extensions, skills, rules, LSP off.
// BENCH_AS_SHIPPED=1 keeps extensions, skills and rules on and sets OMBRA_HARNESS_DIR to BENCH_HARNESS_DIR (default
// the repository's harness/profile; a variant: .temp/lab/variants/<name>), so the harness runs as Ombra ships it.
// BENCH_THINKING=<level> passes `--thinking` (the reasoning effort for codex, `--effort` for Claude Code).
// BENCH_CODEX=1 / BENCH_CLAUDE=1: the side is OpenAI's Codex CLI (`codex exec`, codex.mjs) or Anthropic's Claude Code
// (`claude -p`, claudecode.mjs) instead of the engine; BENCH_MODEL is a model name without provider prefix and
// <engine-dir> is ignored (the runtime still supplies `bun` for tests).
// Cache: a run whose result.json exists in <out-dir> is not repeated (delete it to re-run).
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { claudeMetrics, runClaude } from "./claudecode.mjs";
import { codexMetrics, runCodex } from "./codex.mjs";
import { userIsolationArgs } from "./isolation.mjs";

const [engineArg, rtArg, polyArg, outArg, runsArg = "1"] = process.argv.slice(2);
const MODEL = process.env.BENCH_MODEL;
const CODEX = process.env.BENCH_CODEX === "1";
const CLAUDE = process.env.BENCH_CLAUDE === "1";
const CLI = CODEX || CLAUDE;
if (!engineArg || !rtArg || !polyArg || !outArg || !MODEL || (CLI && MODEL.includes("/")) || (CODEX && CLAUDE)) {
	console.error("usage: BENCH_MODEL=<provider/model> bun polyglot.mjs <engine-dir> <runtime-dir> <polyglot-checkout> <out-dir> [runs]\n       BENCH_CODEX=1 BENCH_MODEL=<codex-model> bun polyglot.mjs - <runtime-dir> <polyglot-checkout> <out-dir> [runs]\n       BENCH_CLAUDE=1 BENCH_MODEL=<claude-model> bun polyglot.mjs - <runtime-dir> <polyglot-checkout> <out-dir> [runs]");
	process.exit(2);
}
const HERE = path.dirname(new URL(import.meta.url).pathname);
const RT = path.resolve(rtArg), POLY = path.resolve(polyArg), OUT = path.resolve(outArg);
const BUN = path.join(RT, "bun/bun");
const RUNS = Number(runsArg);
const TIMEOUT_MS = Number(process.env.BENCH_TIMEOUT_MS ?? 300000);
const AGENT_DIR = process.env.BENCH_AGENT_DIR ?? path.join(os.homedir(), ".omp/agent");
// BENCH_TOOLS: an explicit tool list (also keeps every listed tool top-level); unset = the engine's default set, as
// Ombra runs it. BENCH_CONFIG: a settings overlay file passed as `--config`.
const TOOLS = process.env.BENCH_TOOLS;
const EXTRA = [...(TOOLS ? ["--no-tools", "--tools", TOOLS] : []), ...(process.env.BENCH_CONFIG ? ["--config", path.resolve(process.env.BENCH_CONFIG)] : [])];
const PINNED = JSON.parse(fs.readFileSync(path.join(HERE, "polyglot-subset.json"), "utf8"));
const subset = process.env.BENCH_SUBSET === "all"
	? { ...PINNED, exercises: ["javascript", "python"].flatMap(lang => fs.readdirSync(path.join(POLY, lang, "exercises/practice")).sort().map(name => `${lang}/${name}`)) }
	: process.env.BENCH_SUBSET ? JSON.parse(fs.readFileSync(path.resolve(process.env.BENCH_SUBSET), "utf8")) : PINNED;
const head = spawnSync("git", ["rev-parse", "HEAD"], { cwd: POLY, encoding: "utf8" }).stdout.trim();
if (head !== subset.commit) console.error(`warning: ${POLY} is at ${head || "?"}, the subset pins ${subset.commit}`);
const only = process.env.BENCH_ONLY?.split(",");
const AS_SHIPPED = process.env.BENCH_AS_SHIPPED === "1";
const HARNESS_DIR = path.resolve(process.env.BENCH_HARNESS_DIR ?? path.join(HERE, "../../harness/profile"));
if (AS_SHIPPED && !fs.existsSync(HARNESS_DIR)) throw new Error(`BENCH_HARNESS_DIR ${HARNESS_DIR} does not exist`);
const THINKING = process.env.BENCH_THINKING;

fs.mkdirSync(OUT, { recursive: true });
const omp = path.join(OUT, "rt/omp");
if (!CLI) {
	fs.mkdirSync(omp, { recursive: true });
	spawnSync("rsync", ["-a", "--delete", "--exclude", "node_modules", "--exclude", ".temp", path.resolve(engineArg) + "/", path.join(omp, "engine") + "/"], { stdio: "inherit" });
	for (const f of ["bunfig.toml", "package.json"]) fs.copyFileSync(path.join(RT, "omp", f), path.join(omp, f));
	if (!fs.existsSync(path.join(omp, "node_modules"))) fs.symlinkSync(path.join(RT, "omp/node_modules"), path.join(omp, "node_modules"));
}
const HOME = path.join(OUT, "home");
fs.mkdirSync(HOME, { recursive: true });
const ISOLATION_ARGS = CLI ? [] : userIsolationArgs({ agentDir: AGENT_DIR, harnessDir: AS_SHIPPED ? HARNESS_DIR : undefined, configs: process.env.BENCH_CONFIG ? [path.resolve(process.env.BENCH_CONFIG)] : [], outFile: path.join(OUT, "isolation.yml") });
const TEST_RE = /\b(bun test|unittest|pytest)\b/;

const testCommand = (lang, tests) => (lang === "javascript" ? `bun test ${tests.join(" ")}` : `python3 -m unittest ${tests.map(t => t.replace(/\.py$/, "")).join(" ")}`);
function numstat(a, b) {
	const r = spawnSync("git", ["diff", "--no-index", "--numstat", a, b], { encoding: "utf8" });
	const [added = 0, removed = 0] = (r.stdout.trim().split(/\s+/) ?? []).map(Number);
	return { added: added || 0, removed: removed || 0 };
}
function listFiles(root) {
	const out = [];
	const walk = d => { for (const e of fs.readdirSync(d, { withFileTypes: true })) { if (e.name === "node_modules" || e.name === "__pycache__") continue; const p = path.join(d, e.name); if (e.isDirectory()) walk(p); else out.push(path.relative(root, p)); } };
	walk(root);
	return out.sort();
}

// Work folders outside any repository (see run.mjs): the engine must not pick up this repo's AGENTS.md.
const WORK_ROOT = fs.mkdtempSync(path.join(os.tmpdir(), "harness-polyglot-"));
const results = [];
for (const ex of subset.exercises) for (let r = 0; r < RUNS; r++) {
	if (only && !only.includes(ex)) continue;
	const [lang, name] = ex.split("/");
	const dir = path.join(OUT, "runs", `${lang}-${name}-${r}`);
	const cached = path.join(dir, "result.json");
	if (fs.existsSync(cached)) { results.push(JSON.parse(fs.readFileSync(cached, "utf8"))); continue; }
	const src = path.join(POLY, lang, "exercises/practice", name);
	const config = JSON.parse(fs.readFileSync(path.join(src, ".meta/config.json"), "utf8"));
	const { solution, test: tests } = config.files;
	const work = path.join(WORK_ROOT, `${lang}-${name}-${r}`, "work"), orig = path.join(WORK_ROOT, `${lang}-${name}-${r}`, "orig");
	fs.rmSync(dir, { recursive: true, force: true });
	fs.mkdirSync(dir, { recursive: true });
	for (const target of [work, orig]) {
		fs.cpSync(src, target, { recursive: true, filter: s => !s.includes(`${path.sep}.meta`) });
		for (const t of tests) fs.writeFileSync(path.join(target, t), fs.readFileSync(path.join(target, t), "utf8").replaceAll("xtest(", "test(").replaceAll("xit(", "it("));
	}
	const docs = ["instructions.md", "instructions.append.md"].map(f => path.join(src, ".docs", f)).filter(f => fs.existsSync(f)).map(f => fs.readFileSync(f, "utf8")).join("\n\n");
	const cmd = testCommand(lang, tests);
	const prompt = `${docs}\n\n####\n\nUse the above instructions to modify the supplied files: ${solution.join(", ")}. Keep and implement the existing function or class stubs; they are called from the tests. Only use standard libraries, don't suggest installing any packages. The tests are in ${tests.join(", ")} (do not change them); run them with \`${cmd}\`.`;
	let p;
	if (CODEX) {
		p = await runCodex({ model: MODEL, effort: THINKING, prompt, cwd: work, home: HOME, pathPrefix: `${path.dirname(BUN)}:${process.env.PATH}`, timeoutMs: TIMEOUT_MS });
		fs.writeFileSync(path.join(dir, "rollout.jsonl"), p.rollout);
	} else if (CLAUDE) {
		p = await runClaude({ model: MODEL, effort: THINKING, prompt, cwd: work, home: HOME, pathPrefix: `${path.dirname(BUN)}:${process.env.PATH}`, timeoutMs: TIMEOUT_MS });
	} else {
		const t0 = Date.now();
		const s = spawnSync(BUN, ["--config=" + path.join(omp, "bunfig.toml"), "--no-env-file", "--no-install", path.join(omp, "engine/coding-agent/src/cli.ts"),
			"-p", "--mode", "json", "--approval-mode", "yolo", "--no-session", "--model", MODEL, ...(THINKING ? ["--thinking", THINKING] : []),
			...(AS_SHIPPED ? [] : ["--no-extensions", "--no-skills", "--no-rules"]), "--no-lsp", "--no-prewalk", "--no-title", ...EXTRA, ...ISOLATION_ARGS, prompt], {
			cwd: work, timeout: TIMEOUT_MS, encoding: "utf8", maxBuffer: 64 << 20,
			// NODE_EXTRA_CA_CERTS: behind a TLS-intercepting proxy provider requests fail certificate checks without it.
			env: { PATH: `${path.dirname(BUN)}:${process.env.PATH}`, HOME, PI_CODING_AGENT_DIR: AGENT_DIR, TERM: "dumb", ...(AS_SHIPPED ? { OMBRA_HARNESS_DIR: HARNESS_DIR } : {}), ...(process.env.NODE_EXTRA_CA_CERTS ? { NODE_EXTRA_CA_CERTS: process.env.NODE_EXTRA_CA_CERTS } : {}) },
		});
		p = { stdout: s.stdout ?? "", stderr: s.stderr ?? "", code: s.status, timedOut: s.error?.code === "ETIMEDOUT", wallMs: Date.now() - t0 };
	}
	fs.writeFileSync(path.join(dir, "stdout.jsonl"), p.stdout);
	fs.writeFileSync(path.join(dir, "stderr.txt"), p.stderr);
	const before = listFiles(orig), after = listFiles(work);
	const changed = [...new Set([...before, ...after])].filter(f => !fs.existsSync(path.join(orig, f)) || !fs.existsSync(path.join(work, f)) || fs.readFileSync(path.join(orig, f), "utf8") !== fs.readFileSync(path.join(work, f), "utf8"));
	let added = 0, removed = 0, solutionBytes = 0;
	for (const f of solution) {
		const d = numstat(path.join(orig, f), path.join(work, f));
		added += d.added; removed += d.removed;
		if (fs.existsSync(path.join(work, f))) solutionBytes += fs.statSync(path.join(work, f)).size;
	}
	// Score against the original tests, whatever the agent did to them.
	for (const t of tests) fs.copyFileSync(path.join(orig, t), path.join(work, t));
	const score = spawnSync(lang === "javascript" ? BUN : "python3", lang === "javascript" ? ["test", ...tests] : ["-m", "unittest", ...tests.map(t => t.replace(/\.py$/, ""))], { cwd: work, encoding: "utf8", timeout: 120000 });
	let metrics;
	if (CODEX) metrics = codexMetrics(p, TEST_RE);
	else if (CLAUDE) metrics = claudeMetrics(p, TEST_RE);
	else {
		const events = p.stdout.split("\n").filter(l => l.startsWith("{")).map(l => { try { return JSON.parse(l); } catch { return undefined; } }).filter(Boolean);
		const assistant = events.filter(e => e.type === "message_end" && e.message?.role === "assistant");
		const toolStarts = events.filter(e => e.type === "tool_execution_start");
		const usage = assistant.reduce((u, e) => {
			const x = e.message.usage ?? {};
			return { input: u.input + (x.input ?? 0) + (x.cacheRead ?? 0) + (x.cacheWrite ?? 0), output: u.output + (x.output ?? 0), cost: u.cost + (x.cost?.total ?? 0) };
		}, { input: 0, output: 0, cost: 0 });
		metrics = {
			requests: assistant.length,
			toolCalls: toolStarts.length,
			toolErrors: events.filter(e => e.type === "tool_execution_end" && e.isError).length,
			testRuns: toolStarts.filter(e => e.toolName === "bash" && TEST_RE.test(String(e.args?.command ?? ""))).length,
			inputTokens: usage.input, outputTokens: usage.output, costUsd: Number(usage.cost.toFixed(4)),
		};
	}
	const row = {
		exercise: ex, run: r, model: CODEX ? `codex/${MODEL}` : CLAUDE ? `claude-code/${MODEL}` : MODEL,
		pass: score.status === 0,
		wallMs: p.wallMs,
		exitCode: p.code, timedOut: p.timedOut,
		linesAdded: added, linesRemoved: removed, solutionBytes,
		changedFiles: changed,
		unrequestedEdits: changed.filter(f => !solution.includes(f)).length,
		...metrics,
	};
	fs.writeFileSync(cached, JSON.stringify(row, null, 2));
	results.push(row);
	console.log(JSON.stringify(row));
}
fs.writeFileSync(path.join(OUT, "results.json"), JSON.stringify(results, null, 2));
const sum = k => results.reduce((n, r) => n + (r[k] ?? 0), 0);
const mean = k => (results.length ? sum(k) / results.length : 0);
const table = [
	"| runs | pass | wall s (avg) | lines +/− (avg) | solution bytes (avg) | unrequested edits | requests (avg) | tool errors (avg) | test runs (avg) | input tok (sum) | output tok (sum) | cost $ (sum) |",
	"|---|---|---|---|---|---|---|---|---|---|---|---|",
	`| ${results.length} | ${results.filter(r => r.pass).length}/${results.length} | ${(mean("wallMs") / 1000).toFixed(1)} | +${mean("linesAdded").toFixed(1)}/−${mean("linesRemoved").toFixed(1)} | ${mean("solutionBytes").toFixed(0)} | ${sum("unrequestedEdits")} | ${mean("requests").toFixed(1)} | ${mean("toolErrors").toFixed(1)} | ${mean("testRuns").toFixed(1)} | ${sum("inputTokens")} | ${sum("outputTokens")} | ${sum("costUsd").toFixed(2)} |`,
].join("\n");
fs.writeFileSync(path.join(OUT, "summary.md"), table + "\n");
console.log(table);
