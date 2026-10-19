// Harness benchmark runner: runs every task under every fault N times against one engine tree, using the
// scripted model (model.mjs), and writes <out>/results.json plus a summary table.
//
//   bun run.mjs <engine-dir> <installed-runtime-dir> <out-dir> [runs=3] [faults=all] [tasks=all]
//
// <engine-dir>: a runtime/engine tree (baseline snapshot or the working tree); copied, never modified.
// <installed-runtime-dir>: ombra-engine-…-v1 with bun/ and omp/node_modules (pinned dependencies).
//
// Baseline vs working tree: snapshot the engine before changing it (`git archive HEAD runtime/engine | tar -x -C …`
// once runtime/engine is committed; until then `rsync -a --exclude node_modules runtime/engine/ <snapshot>/`), run
// this script on the snapshot and on runtime/engine with the same runs/faults, then `bun compare.mjs <a> <b>`.
// Metrics per run (from the model's request log): requests, failed requests, input bytes (sum and per request),
// tokens ≈ bytes/4 (estimate, not a tokenizer), system prompt and tool schema bytes, tool calls, wall time,
// success (the task's check prints OK afterwards) and whether the engine exited.
import { spawnSync, spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { TASKS } from "./tasks.mjs";

const [engineArg, rtArg, outArg, runsArg = "3", faultsArg = "all", tasksArg = "all"] = process.argv.slice(2);
if (!engineArg || !rtArg || !outArg) { console.error("usage: bun run.mjs <engine-dir> <runtime-dir> <out-dir> [runs] [faults] [tasks]"); process.exit(2); }
const HERE = path.dirname(new URL(import.meta.url).pathname);
const REPO = path.resolve(HERE, "../..");
const RT = path.resolve(rtArg), OUT = path.resolve(outArg);
const RUNS = Number(runsArg);
const FAULTS = faultsArg === "all" ? ["none", "rate-limit", "rate-limit-body", "server-error", "error-burst", "stream-drop", "empty-reply", "bad-args", "small-context"] : faultsArg.split(",");
const TASK_IDS = tasksArg === "all" ? Object.keys(TASKS) : tasksArg.split(",");
const BUN = path.join(RT, "bun/bun");
const TIMEOUT_MS = Number(process.env.BENCH_TIMEOUT_MS ?? 90000);

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });
// Engine copy with the pinned dependencies, as avalonia/tools/verify/feature-smoke/run.sh does.
const omp = path.join(OUT, "rt/omp");
fs.mkdirSync(omp, { recursive: true });
spawnSync("rsync", ["-a", "--delete", "--exclude", "node_modules", "--exclude", ".temp", path.resolve(engineArg) + "/", path.join(omp, "engine") + "/"], { stdio: "inherit" });
for (const f of ["bunfig.toml", "package.json"]) fs.copyFileSync(path.join(RT, "omp", f), path.join(omp, f));
fs.symlinkSync(path.join(RT, "omp/node_modules"), path.join(omp, "node_modules"));

// Real model (BENCH_MODEL=<provider/model>): no scripted server; the user's existing agent dir supplies the
// credentials (read as any second omp process does; nothing here logs or copies them), HOME stays isolated, and
// extensions/skills/rules/LSP are off so both sides see the same tool set. Only the "none" fault applies.
const REAL_MODEL = process.env.BENCH_MODEL;
const REAL_AGENT_DIR = process.env.BENCH_AGENT_DIR ?? path.join(os.homedir(), ".omp/agent");
// BENCH_TOOLS: an explicit tool list (also keeps every listed tool top-level); unset = the engine's default set, as
// Ombra runs it. BENCH_CONFIG: a settings overlay file passed as `--config`.
const TOOLS = process.env.BENCH_TOOLS;
const APPROVAL = process.env.BENCH_APPROVAL ?? "yolo";
const PORT = Number(process.env.BENCH_PORT ?? 18391);
const HOME = path.join(OUT, "home");
if (!REAL_MODEL) spawnSync(path.join(REPO, "avalonia/tools/stand-in-env/setup.sh"), [HOME, String(PORT)], { stdio: "ignore" });
else fs.mkdirSync(HOME, { recursive: true });

const sleep = ms => new Promise(r => setTimeout(r, ms));
async function waitPort() {
	for (let k = 0; k < 100; k++) { try { await fetch(`http://127.0.0.1:${PORT}/v1/models`); return; } catch { await sleep(50); } }
	throw new Error("model did not start");
}
// Every file under `root` with its content, for the before/after diff.
function snapshot(root) {
	const out = new Map();
	const walk = d => { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p); else out.set(path.relative(root, p), fs.readFileSync(p, "utf8")); } };
	if (fs.existsSync(root)) walk(root);
	return out;
}
function changed(before, after) {
	const names = new Set([...before.keys(), ...after.keys()]);
	return [...names].filter(n => before.get(n) !== after.get(n)).sort();
}

// Work folders live outside any repository: under <out-dir> inside this repo the engine would also load the repo's
// own AGENTS.md (found walking up from the cwd), which no user project carries.
const WORK_ROOT = fs.mkdtempSync(path.join(os.tmpdir(), "harness-bench-"));
const results = [];
for (const fault of REAL_MODEL ? ["none"] : FAULTS) for (const id of TASK_IDS) for (let r = 0; r < RUNS; r++) {
	const task = TASKS[id];
	const dir = path.join(OUT, "runs", `${fault}-${id}-${r}`);
	const work = path.join(WORK_ROOT, `${fault}-${id}-${r}`, "work");
	// A sibling folder the task never needs: any change there is an action outside the workspace.
	const outside = path.join(WORK_ROOT, `${fault}-${id}-${r}`, "outside");
	fs.mkdirSync(dir, { recursive: true });
	fs.mkdirSync(work, { recursive: true });
	fs.mkdirSync(outside, { recursive: true });
	for (const [f, c] of Object.entries(task.files)) { fs.mkdirSync(path.dirname(path.join(work, f)), { recursive: true }); fs.writeFileSync(path.join(work, f), c.replaceAll("{outside}", outside)); }
	fs.writeFileSync(path.join(outside, "keep.txt"), "must survive\n");
	const before = snapshot(work), outsideBefore = snapshot(outside);
	const metrics = path.join(dir, "requests.jsonl");
	fs.writeFileSync(metrics, "");
	const model = REAL_MODEL ? undefined : spawn(BUN, [path.join(HERE, "model.mjs"), String(PORT), metrics, fault], { stdio: ["ignore", "ignore", "ignore"] });
	if (model) await waitPort();
	const t0 = Date.now();
	const realArgs = REAL_MODEL ? ["--model", REAL_MODEL, "--no-extensions", "--no-skills", "--no-rules", "--no-lsp", "--no-prewalk", "--no-title", ...(TOOLS ? ["--no-tools", "--tools", TOOLS] : [])] : [];
	if (process.env.BENCH_CONFIG) realArgs.push("--config", path.resolve(process.env.BENCH_CONFIG));
	const env = REAL_MODEL
		? { PATH: `${path.dirname(BUN)}:${process.env.PATH}`, HOME, PI_CODING_AGENT_DIR: REAL_AGENT_DIR, TERM: "dumb" }
		: { PATH: `${path.dirname(BUN)}:${process.env.PATH}`, HOME, OMP_PROFILE: "harness", QWEN_LOCAL_API_KEY: "dummy-not-a-secret", TERM: "dumb" };
	const p = spawnSync(BUN, ["--config=" + path.join(omp, "bunfig.toml"), "--no-env-file", "--no-install", path.join(omp, "engine/coding-agent/src/cli.ts"),
		"-p", "--mode", "json", "--approval-mode", APPROVAL, "--no-session", ...realArgs, task.prompt.replace("{outside}", outside)], {
		cwd: work, timeout: TIMEOUT_MS, encoding: "utf8", maxBuffer: 64 << 20, env,
	});
	const wall = Date.now() - t0;
	if (model) { model.kill(); await new Promise(r => model.once("exit", r)); }
	fs.writeFileSync(path.join(dir, "stdout.jsonl"), p.stdout ?? "");
	fs.writeFileSync(path.join(dir, "stderr.txt"), p.stderr ?? "");
	const changedFiles = changed(before, snapshot(work));
	const check = spawnSync(BUN, ["check.mjs"], { cwd: work, encoding: "utf8" });
	const reqs = fs.readFileSync(metrics, "utf8").trim().split("\n").filter(Boolean).map(l => JSON.parse(l));
	const ok = reqs.filter(q => q.status === 200);
	// Provider-reported usage and tool calls from the engine's own event stream (real and scripted runs alike).
	const events = (p.stdout ?? "").split("\n").filter(l => l.startsWith("{")).map(l => { try { return JSON.parse(l); } catch { return undefined; } }).filter(Boolean);
	const assistantEnds = events.filter(e => e.type === "message_end" && e.message?.role === "assistant");
	const usage = assistantEnds.reduce((u, e) => { const x = e.message.usage ?? {}; return { input: u.input + (x.input ?? 0) + (x.cacheRead ?? 0) + (x.cacheWrite ?? 0), output: u.output + (x.output ?? 0) }; }, { input: 0, output: 0 });
	const finalText = (assistantEnds.at(-1)?.message?.content ?? []).map(c => c.text ?? "").join("");
	const row = {
		fault, task: id, run: r, model: REAL_MODEL ?? "scripted",
		success: (check.stdout ?? "").trim() === "OK",
		exitCode: p.status, timedOut: p.error?.code === "ETIMEDOUT",
		finalSeen: REAL_MODEL ? finalText.trim().length > 0 : /DONE: fixed and checked/.test(p.stdout ?? ""),
		changedFiles,
		unrequestedEdits: changedFiles.filter(f => !task.allowedChanges.includes(f)).length,
		outsideTouched: changed(outsideBefore, snapshot(outside)).length,
		turns: assistantEnds.length,
		toolCallsSeen: events.filter(e => e.type === "tool_execution_start").length,
		providerInputTokens: usage.input,
		providerOutputTokens: usage.output,
		requests: REAL_MODEL ? assistantEnds.length : reqs.length,
		failedRequests: reqs.filter(q => q.status !== 200).length,
		inputBytes: reqs.reduce((n, q) => n + q.bytes, 0),
		maxRequestBytes: Math.max(0, ...reqs.map(q => q.bytes)),
		systemBytes: reqs[0]?.systemBytes ?? 0,
		toolsBytes: reqs[0]?.toolsBytes ?? 0,
		toolCalls: ok.filter(q => q.step < 3).length,
		wallMs: wall,
		firstRetryGapMs: reqs.length > 1 && reqs[0].status !== 200 ? reqs[1].t - reqs[0].t : null,
	};
	row.inputTokensEst = Math.round(row.inputBytes / 4);
	results.push(row);
	console.log(JSON.stringify(row));
}
fs.writeFileSync(path.join(OUT, "results.json"), JSON.stringify(results, null, 2));

// Scripted runs group by fault (the harness under each failure); real-model runs by task (the model's behaviour).
const groups = Map.groupBy(results, r => (REAL_MODEL ? r.task : r.fault));
const avg = (a, k) => Math.round(a.reduce((n, r) => n + (r[k] ?? 0), 0) / a.length);
let table = `| ${REAL_MODEL ? "task" : "fault"} | runs | success | final answer | unrequested edits | outside touched | requests (avg) | input KB (avg) | provider input tok (avg) | max request KB | system KB | tools KB | wall s (avg) |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|\n`;
for (const [f, a] of groups) table += `| ${f} | ${a.length} | ${a.filter(r => r.success).length}/${a.length} | ${a.filter(r => r.finalSeen).length}/${a.length} | ${a.reduce((n, r) => n + r.unrequestedEdits, 0)} | ${a.filter(r => r.outsideTouched > 0).length}/${a.length} | ${(a.reduce((n, r) => n + r.requests, 0) / a.length).toFixed(1)} | ${(avg(a, "inputBytes") / 1024).toFixed(1)} | ${avg(a, "providerInputTokens")} | ${(avg(a, "maxRequestBytes") / 1024).toFixed(1)} | ${(avg(a, "systemBytes") / 1024).toFixed(1)} | ${(avg(a, "toolsBytes") / 1024).toFixed(1)} | ${(avg(a, "wallMs") / 1000).toFixed(1)} |\n`;
fs.writeFileSync(path.join(OUT, "summary.md"), table);
console.log(table);
