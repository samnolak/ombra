// OpenAI Codex CLI as a bench side (the market comparison): `codex exec` with the same model, prompt, timeout and
// working folder as the engine sides. Used by swebench.mjs (`<name>=codex=<model>=<effort>`) and polyglot.mjs
// (BENCH_CODEX=1).
//
// Setup (once): `npm i @openai/codex` into .temp/lab/codex (Node ≥ 18 at ~/.local/bin/node) and a template folder
// .temp/lab/codex-home whose only entry is `auth.json` → ~/.codex/auth.json (symlink; the user's Codex login).
//   BENCH_CODEX_BIN   codex launcher (default .temp/lab/codex/node_modules/.bin/codex)
//   BENCH_CODEX_HOME  the template folder (default .temp/lab/codex-home)
//   BENCH_NODE        node binary for the launcher (default ~/.local/bin/node)
//
// Isolation: every run gets a fresh CODEX_HOME under .temp/lab/codex-runs holding only a symlink to the template's
// auth.json target, so the user's ~/.codex config.toml, AGENTS.md, rules, skills and plugins stay out; it is removed
// after the run (its session rollout is kept as rollout.jsonl). Should Codex replace the symlink by a refreshed
// auth.json, that file is moved back onto the target rather than deleted. Credentials are never copied or printed.
// HOME is the side's isolated home. Flags: full access without approvals or sandbox (the yolo equivalent), no web
// search, browser or computer use, `allow_login_shell=false` (a login zsh runs path_helper, which puts /usr/bin
// ahead of the PATH wrappers), `--ignore-user-config`, `--ignore-rules`, `--skip-git-repo-check`.
//
// Metrics: the `--json` event stream (command_execution items: commands, failed commands, test runs; turn.completed
// usage: tokens) and the rollout (one `token_count` event per model response: requests; function/custom tool calls).
// input tokens include cached input, as the engine sides' `inputTokens` do.
import { spawn } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname), "../..");
const CODEX_BIN = path.resolve(process.env.BENCH_CODEX_BIN ?? path.join(REPO, ".temp/lab/codex/node_modules/.bin/codex"));
const CODEX_HOME_TEMPLATE = path.resolve(process.env.BENCH_CODEX_HOME ?? path.join(REPO, ".temp/lab/codex-home"));
const RUNS_ROOT = path.join(REPO, ".temp/lab/codex-runs");
export const NODE_DIR = path.dirname(process.env.BENCH_NODE ?? path.join(os.homedir(), ".local/bin/node"));

const authTarget = () => {
	const link = path.join(CODEX_HOME_TEMPLATE, "auth.json");
	if (!fs.lstatSync(link, { throwIfNoEntry: false })?.isSymbolicLink()) throw new Error(`${link} must be a symlink to the Codex auth.json`);
	return path.resolve(CODEX_HOME_TEMPLATE, fs.readlinkSync(link));
};

/**
 * Runs `codex exec` in `cwd`; resolves to { stdout, stderr, rollout, code, timedOut, wallMs }.
 * `pathPrefix` goes before the node and system dirs on PATH; `env` adds variables (DOCKER_HOST, …).
 */
export function runCodex({ model, effort, prompt, cwd, home, pathPrefix = "", timeoutMs, env = {} }) {
	const target = authTarget();
	const codexHome = path.join(RUNS_ROOT, crypto.randomUUID());
	fs.mkdirSync(codexHome, { recursive: true });
	fs.symlinkSync(target, path.join(codexHome, "auth.json"));
	const args = ["exec", "--json", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox",
		"-m", model, ...(effort ? ["-c", `model_reasoning_effort="${effort}"`] : []),
		"-c", 'web_search="disabled"', "-c", "allow_login_shell=false",
		"--disable", "browser_use", "--disable", "browser_use_external", "--disable", "in_app_browser", "--disable", "computer_use",
		prompt];
	return new Promise(resolve => {
		const t0 = Date.now();
		const p = spawn("nice", ["-n", "10", path.join(NODE_DIR, "node"), CODEX_BIN, ...args], {
			cwd, detached: true,
			env: {
				PATH: [pathPrefix, NODE_DIR, "/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"].filter(Boolean).join(":"),
				HOME: home, CODEX_HOME: codexHome, TERM: "dumb", ...env,
				...(process.env.NODE_EXTRA_CA_CERTS ? { NODE_EXTRA_CA_CERTS: process.env.NODE_EXTRA_CA_CERTS } : {}),
			},
			stdio: ["ignore", "pipe", "pipe"],
		});
		let stdout = "", stderr = "", timedOut = false;
		p.stdout.on("data", d => (stdout += d));
		p.stderr.on("data", d => (stderr += d));
		const timer = setTimeout(() => { timedOut = true; try { process.kill(-p.pid, "SIGKILL"); } catch {} }, timeoutMs);
		p.on("close", code => {
			clearTimeout(timer);
			const wallMs = Date.now() - t0;
			const sessions = path.join(codexHome, "sessions");
			const rollout = fs.existsSync(sessions) ? fs.readdirSync(sessions, { recursive: true }).filter(f => String(f).endsWith(".jsonl")).map(f => fs.readFileSync(path.join(sessions, String(f)), "utf8")).join("") : "";
			const auth = path.join(codexHome, "auth.json");
			if (!fs.lstatSync(auth, { throwIfNoEntry: false })?.isSymbolicLink() && fs.existsSync(auth)) {
				fs.renameSync(auth, target);
				console.error("codex replaced the auth.json symlink (token refresh); moved it back onto the target");
			}
			fs.rmSync(codexHome, { recursive: true, force: true });
			resolve({ stdout, stderr, rollout, code, timedOut, wallMs });
		});
	});
}

const jsonLines = s => s.split("\n").filter(l => l.startsWith("{")).map(l => { try { return JSON.parse(l); } catch { return undefined; } }).filter(Boolean);

/** Bench columns from a codex run; `testRe` classifies test commands. */
export function codexMetrics({ stdout, rollout }, testRe) {
	const events = jsonLines(stdout);
	const commands = events.filter(e => e.type === "item.completed" && e.item?.type === "command_execution").map(e => e.item);
	const usage = events.filter(e => e.type === "turn.completed").reduce((u, e) => ({
		input: u.input + (e.usage?.input_tokens ?? 0), cached: u.cached + (e.usage?.cached_input_tokens ?? 0),
		output: u.output + (e.usage?.output_tokens ?? 0), reasoning: u.reasoning + (e.usage?.reasoning_output_tokens ?? 0),
	}), { input: 0, cached: 0, output: 0, reasoning: 0 });
	const records = jsonLines(rollout);
	const failure = events.findLast(e => e.type === "turn.failed" || e.type === "error");
	return {
		requests: records.filter(r => r.type === "event_msg" && r.payload?.type === "token_count" && r.payload.info).length,
		toolCalls: records.filter(r => r.type === "response_item" && ["function_call", "custom_tool_call", "local_shell_call"].includes(r.payload?.type)).length,
		commands: commands.length,
		toolErrors: commands.filter(c => c.status === "failed" || (c.exit_code ?? 0) !== 0).length,
		testRuns: commands.filter(c => testRe.test(String(c.command ?? ""))).length,
		fileChanges: events.filter(e => e.type === "item.completed" && e.item?.type === "file_change").length,
		inputTokens: usage.input, cacheReadTokens: usage.cached, outputTokens: usage.output, reasoningTokens: usage.reasoning, costUsd: 0,
		stopReason: failure ? "error" : events.some(e => e.type === "turn.completed") ? "stop" : undefined,
		error: failure ? (failure.error?.message ?? failure.message) : undefined,
	};
}
