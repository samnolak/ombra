// Anthropic's Claude Code CLI as a bench side (the market comparison for Anthropic models): `claude -p` with the same
// model, prompt, timeout and working folder as the engine sides. Used by swebench.mjs (`<name>=claude=<model>=<effort>`)
// and polyglot.mjs (BENCH_CLAUDE=1).
//
// Setup (once): `npm i @anthropic-ai/claude-code` into .temp/lab/claude (the package installs a native binary; no Node
// needed at run time).
//   BENCH_CLAUDE_BIN  claude launcher (default .temp/lab/claude/node_modules/.bin/claude)
//
// Auth: CLAUDE_CODE_OAUTH_TOKEN, when the bench's own environment sets it; otherwise the Anthropic OAuth access token
// that omp keeps in <agent-dir>/agent.db (BENCH_AGENT_DIR, PI_CODING_AGENT_DIR, default ~/.omp/agent; table
// auth_credentials, read-only, fresh at every run start, since omp refreshes it). Only the access token goes to the
// child, never the refresh token, so Claude Code cannot rotate the user's login. It goes through a pipe on fd 3
// (CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR=3), not the environment, so the agent's Bash commands do not inherit it; it
// is never printed or written. A token that expires during a run makes that run fail (an omp process refreshes it; this
// side does not).
//
// Isolation: every run gets a fresh CLAUDE_CONFIG_DIR under .temp/lab/claude-runs (no user settings, CLAUDE.md, memory,
// skills, plugins, hooks or agents), removed after the run; HOME is the side's isolated home. Flags: every permission
// skipped (the yolo equivalent), `--strict-mcp-config` and ENABLE_CLAUDEAI_MCP_SERVERS=false (no MCP servers, the
// account's claude.ai connectors included), WebSearch and WebFetch disallowed, `--no-session-persistence`, autoupdate,
// telemetry, error reporting and other nonessential traffic off. Not `--bare`: the side is Claude Code as people use
// it. The prompt goes on stdin (`--disallowedTools` is variadic and would take a positional prompt as a tool name).
// SHELL=/bin/bash: the Bash tool runs commands after sourcing a shell snapshot that exports the CLI's own PATH (no login
// shell, so macOS path_helper does not put /usr/bin ahead of the PATH wrappers); an empty HOME has no ~/.bashrc.
//
// Metrics: the `stream-json` events (subagent messages included, `--forward-subagent-text`): requests = distinct
// assistant message ids, tool calls = tool_use blocks, commands = Bash tool_use, tool errors = tool_result with
// is_error, test runs = Bash commands matching the test pattern, file changes = Edit/MultiEdit/Write/NotebookEdit
// calls. Tokens and cost from the final `result` event's modelUsage, summed over all models (side calls on a smaller
// model included); input tokens include cache reads and cache writes, as the engine sides' `inputTokens` do;
// reasoningTokens = modelUsage thinkingTokens (also inside outputTokens). costUsd is the API-equivalent
// `total_cost_usd` (the subscription pays).
import { Database } from "bun:sqlite";
import { spawn } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname), "../..");
const CLAUDE_BIN = path.resolve(process.env.BENCH_CLAUDE_BIN ?? path.join(REPO, ".temp/lab/claude/node_modules/.bin/claude"));
const RUNS_ROOT = path.join(REPO, ".temp/lab/claude-runs");
const AGENT_DIR = process.env.BENCH_AGENT_DIR ?? process.env.PI_CODING_AGENT_DIR ?? path.join(os.homedir(), ".omp/agent");

function oauthToken(timeoutMs) {
	if (process.env.CLAUDE_CODE_OAUTH_TOKEN) return process.env.CLAUDE_CODE_OAUTH_TOKEN;
	const db = new Database(path.join(AGENT_DIR, "agent.db"), { readonly: true });
	try {
		const row = db.query("SELECT data FROM auth_credentials WHERE provider = 'anthropic' AND disabled_cause IS NULL ORDER BY updated_at DESC LIMIT 1").get();
		const { access, expires } = row ? JSON.parse(row.data) : {};
		if (!access) throw new Error(`no Anthropic OAuth access token in ${AGENT_DIR}/agent.db (log in to Anthropic in omp)`);
		if (expires && expires < Date.now()) throw new Error(`the Anthropic OAuth token in ${AGENT_DIR}/agent.db has expired; any omp request refreshes it`);
		if (expires && expires < Date.now() + timeoutMs) console.error(`warning: the Anthropic OAuth token expires in ${Math.round((expires - Date.now()) / 60000)} min, before this run's timeout`);
		return access;
	} finally { db.close(); }
}

/**
 * Runs `claude -p` in `cwd`; resolves to { stdout, stderr, code, timedOut, wallMs }.
 * `pathPrefix` goes before the system dirs on PATH; `env` adds variables (DOCKER_HOST, …).
 */
export function runClaude({ model, effort, prompt, cwd, home, pathPrefix = "", timeoutMs, env = {} }) {
	const token = oauthToken(timeoutMs);
	const configDir = path.join(RUNS_ROOT, crypto.randomUUID());
	fs.mkdirSync(configDir, { recursive: true });
	const args = ["-p", "--output-format", "stream-json", "--verbose", "--forward-subagent-text", "--model", model, ...(effort ? ["--effort", effort] : []),
		"--dangerously-skip-permissions", "--strict-mcp-config", "--no-session-persistence", "--disallowedTools", "WebSearch", "WebFetch"];
	return new Promise(resolve => {
		const t0 = Date.now();
		const p = spawn("nice", ["-n", "10", CLAUDE_BIN, ...args], {
			cwd, detached: true,
			env: {
				PATH: [pathPrefix, "/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"].filter(Boolean).join(":"),
				HOME: home, SHELL: "/bin/bash", TERM: "dumb", CLAUDE_CONFIG_DIR: configDir, CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR: "3",
				DISABLE_AUTOUPDATER: "1", DISABLE_TELEMETRY: "1", DISABLE_ERROR_REPORTING: "1", CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: "1",
				ENABLE_CLAUDEAI_MCP_SERVERS: "false", ...env,
				...(process.env.NODE_EXTRA_CA_CERTS ? { NODE_EXTRA_CA_CERTS: process.env.NODE_EXTRA_CA_CERTS } : {}),
			},
			stdio: ["pipe", "pipe", "pipe", "pipe"],
		});
		let stdout = "", stderr = "", timedOut = false;
		p.stdout.on("data", d => (stdout += d));
		p.stderr.on("data", d => (stderr += d));
		for (const s of [p.stdin, p.stdio[3]]) s.on("error", () => {});
		p.stdio[3].end(`${token}\n`);
		p.stdin.end(prompt);
		const timer = setTimeout(() => { timedOut = true; try { process.kill(-p.pid, "SIGKILL"); } catch {} }, timeoutMs);
		p.on("close", (code, signal) => {
			clearTimeout(timer);
			fs.rmSync(configDir, { recursive: true, force: true });
			if (signal && !timedOut) stderr += `\n[harness-bench] claude ended on ${signal}\n`;
			resolve({ stdout, stderr, code, timedOut, wallMs: Date.now() - t0 });
		});
	});
}

const jsonLines = s => s.split("\n").filter(l => l.startsWith("{")).map(l => { try { return JSON.parse(l); } catch { return undefined; } }).filter(Boolean);
const FILE_TOOLS = new Set(["Edit", "MultiEdit", "Write", "NotebookEdit"]);

/** Bench columns from a claude run; `testRe` classifies test commands. */
export function claudeMetrics({ stdout }, testRe) {
	const events = jsonLines(stdout);
	const assistant = events.filter(e => e.type === "assistant" && e.message);
	// stream-json emits one event per content block, so blocks of one response share its message id.
	const toolUses = [...new Map(assistant.flatMap(e => (Array.isArray(e.message.content) ? e.message.content : [])).filter(b => b.type === "tool_use").map(b => [b.id, b])).values()];
	const results = [...new Map(events.filter(e => e.type === "user" && Array.isArray(e.message?.content)).flatMap(e => e.message.content).filter(b => b.type === "tool_result").map(b => [b.tool_use_id, b])).values()];
	const commands = toolUses.filter(b => b.name === "Bash");
	const result = events.findLast(e => e.type === "result");
	const usage = Object.values(result?.modelUsage ?? {}).reduce((u, m) => ({
		input: u.input + (m.inputTokens ?? 0) + (m.cacheReadInputTokens ?? 0) + (m.cacheCreationInputTokens ?? 0),
		cached: u.cached + (m.cacheReadInputTokens ?? 0), output: u.output + (m.outputTokens ?? 0), reasoning: u.reasoning + (m.thinkingTokens ?? 0),
	}), { input: 0, cached: 0, output: 0, reasoning: 0 });
	const failed = result && (result.is_error || result.subtype !== "success");
	return {
		requests: new Set(assistant.map(e => e.message.id).filter(Boolean)).size,
		toolCalls: toolUses.length,
		commands: commands.length,
		toolErrors: results.filter(b => b.is_error).length,
		testRuns: commands.filter(b => testRe.test(String(b.input?.command ?? ""))).length,
		fileChanges: toolUses.filter(b => FILE_TOOLS.has(b.name)).length,
		inputTokens: usage.input, cacheReadTokens: usage.cached, outputTokens: usage.output, reasoningTokens: usage.reasoning,
		costUsd: Number((result?.total_cost_usd ?? 0).toFixed(4)),
		stopReason: failed ? "error" : result ? "stop" : undefined,
		error: failed ? (result.errors?.join("; ") || (typeof result.result === "string" ? result.result.slice(0, 500) : undefined) || result.subtype) : undefined,
	};
}
