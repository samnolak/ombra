// Harness benchmark model: an OpenAI-compatible chat-completions server with a FIXED policy and fault injection.
// It is not a model. Every task runs the same script, so differences between two engines come from the harness only.
//
//   bun model.mjs <port> <metrics.jsonl> <fault>
//
// Policy (step = number of tool results after the last user message):
//   0 → `read` of the task's source file
//   1 → `write` of the fixed file (content from tasks.mjs); a task with `edits` instead changes those lines through
//       `edit` in the variant the engine offers (recognized from the tool schema): one call for hashline, patch,
//       apply_patch and sloppy, one call per site for replace
//   next → `bash` running the task's check command
//   then → final answer "DONE"
// The task is recognized by the token TASK:<id> in the first user message.
//
// Faults (by request index within the run, 1-based):
//   none             no faults
//   rate-limit       request 1 → 429 with Retry-After: 2 (seconds) and an OpenAI-style rate_limit body
//   rate-limit-body  request 1 → 429 without Retry-After; only the body says "Please try again in 3s."
//   server-error     request 2 → 503 "upstream overloaded"
//   error-burst      requests 2–4 → 503 (three in a row)
//   stream-drop      request 2 → 200, streams some text, then the socket is destroyed without [DONE]
//   empty-reply      request 2 → 200 with no content and finish_reason "stop"
//   bad-args         request 2 → tool call whose arguments are truncated JSON
//   small-context    every request above BENCH_SMALL_CONTEXT (default 12000) estimated tokens → 400
//                    context_length_exceeded naming that limit (the server's real window < the configured one)
//
// Each request appends one JSON line to <metrics.jsonl> (`tool`: the tool this reply calls, null for a final answer):
//   { i, t, status, fault, bytes, systemBytes, toolsBytes, messages, toolResultBytes, step, tool, editMode, roles }
// BENCH_DUMP=1 also writes each request body to <metrics.jsonl>.<i>.json.
import http from "node:http";
import fs from "node:fs";
import { TASKS } from "./tasks.mjs";

const port = Number(process.argv[2]);
const metricsPath = process.argv[3];
const fault = process.argv[4] ?? "none";
let count = 0;
// small-context: the server's real window is smaller than the configured 131072 (a local server started with a
// short context); every request estimated (bytes/4) above it is rejected as context_length_exceeded.
const SMALL_CONTEXT = Number(process.env.BENCH_SMALL_CONTEXT ?? 12000);

function textOf(c) {
	if (typeof c === "string") return c;
	if (Array.isArray(c)) return c.map(p => (typeof p === "string" ? p : (p?.text ?? ""))).join("");
	return "";
}

// The engine's edit variant, recognized from the `edit` tool schema it sends (the world, not the bench's setting).
function editModeOf(tools) {
	const fn = (tools ?? []).find(t => t.function?.name === "edit")?.function;
	if (!fn) return null;
	const props = fn.parameters?.properties ?? {};
	if ("old_string" in props) return "replace";
	if ("edits" in props) return "patch";
	const d = fn.description ?? "";
	if (d.startsWith("Hashline")) return "hashline";
	if (d.includes("*** Begin Patch")) return "apply_patch";
	if (d.includes("*** Edit File:")) return "sloppy";
	return "unknown";
}

// Edit-task steps in the given variant's own syntax. Every site is one whole, unique line of the original file.
function editSteps(task, mode, msgs) {
	const { file, edits } = task;
	const lines = task.files[file].split("\n");
	if (mode === "replace") return edits.map(([o, n]) => ({ tool: "edit", args: { i: "Editing", path: file, old_string: o, new_string: n } }));
	if (mode === "patch") return [{ tool: "edit", args: { i: "Editing", path: file, edits: [{ op: "update", diff: edits.map(([o, n]) => `@@\n-${o}\n+${n}\n`).join("") }] } }];
	if (mode === "apply_patch") return [{ tool: "edit", args: { i: "Editing", input: `*** Begin Patch\n*** Update File: ${file}\n${edits.map(([o, n]) => `@@\n-${o}\n+${n}\n`).join("")}*** End Patch\n` } }];
	if (mode === "sloppy") return [{ tool: "edit", args: { i: "Editing", input: `*** Edit File: ${file}\n${edits.map(([o, n]) => `*** Find\n${o}\n*** Replace\n${n}\n`).join("")}` } }];
	if (mode === "hashline") {
		// Snapshot tag from the latest read/write result header `[file#TAG]`.
		const results = msgs.filter(m => m.role === "tool").map(m => textOf(m.content)).join("\n");
		const at = results.lastIndexOf(`[${file}#`);
		const tag = at < 0 ? "0000" : results.slice(at + file.length + 2, at + file.length + 6);
		const ops = edits.map(([o, n]) => { const k = lines.indexOf(o) + 1; return `PUT ${k}.=${k}:\n+${n}`; });
		return [{ tool: "edit", args: { i: "Editing", input: `[${file}#${tag}]\n${ops.join("\n")}\n` } }];
	}
	return [{ tool: "write", args: { path: file, content: task.fixed } }];
}

function decide(body) {
	const msgs = body.messages ?? [];
	const users = msgs.filter(m => m.role === "user");
	const all = users.map(u => textOf(u.content)).join("\n");
	const id = /TASK:([\w-]+)/.exec(all)?.[1];
	const task = TASKS[id];
	if (!task) return { step: 0, text: "No task." };
	// Steps count from the last user message that states the task: harness-injected user messages (stream-drop
	// <system-injection>, compaction "Resume …" continuations) are not a new task, so the script keeps its place.
	let lastUserIdx = msgs.findLastIndex(m => m.role === "user" && /TASK:/.test(textOf(m.content)));
	if (lastUserIdx < 0) lastUserIdx = msgs.findLastIndex(m => m.role === "user" && !textOf(m.content).trimStart().startsWith("<system-injection>"));
	// A tool result that reports invalid arguments does not advance the script: the model repeats that step.
	const step = msgs.slice(lastUserIdx + 1).filter(m => m.role === "tool" && !/Validation failed|not valid JSON/i.test(textOf(m.content))).length;
	const allText = msgs.map(m => textOf(m.content)).join("\n");
	const outside = /rm -rf (\S+)\/\*/.exec(allText)?.[1] ?? "/nonexistent-outside";
	const steps = task.script?.(task, outside) ?? [
		{ tool: "read", args: { path: task.read ?? task.file } },
		...(task.edits ? editSteps(task, editModeOf(body.tools), msgs) : [{ tool: "write", args: { path: task.file, content: task.fixed } }]),
		{ tool: "bash", args: { command: task.check } },
	];
	if (step < steps.length) return { step, text: `Step ${step + 1}.`, tool: { name: steps[step].tool, args: steps[step].args } };
	return { step, text: "DONE: fixed and checked." };
}

const sleep = ms => new Promise(r => setTimeout(r, ms));
function chunk(res, delta, finish = null) {
	res.write(`data: ${JSON.stringify({ id: "b", object: "chat.completion.chunk", created: 0, model: "bench", choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`);
}

http.createServer((req, res) => {
	if (req.method === "GET") { res.writeHead(200, { "content-type": "application/json" }); res.end(JSON.stringify({ data: [{ id: "flash-next-w4a16", object: "model" }] })); return; }
	let raw = "";
	req.on("data", d => (raw += d));
	req.on("end", async () => {
		const i = ++count;
		let body = {};
		try { body = JSON.parse(raw); } catch {}
		const sys = (body.messages ?? []).filter(m => m.role === "system" || m.role === "developer").map(m => textOf(m.content)).join("");
		const toolResultBytes = (body.messages ?? []).filter(m => m.role === "tool").reduce((n, m) => n + textOf(m.content).length, 0);
		const d = decide(body);
		let applied = null;
		const roles = (body.messages ?? []).map(m => m.role[0] + (m.role === "user" ? `:${textOf(m.content).slice(0, 60)}` : "")).join(" | ");
		const rec = status => fs.appendFileSync(metricsPath, JSON.stringify({ i, t: Date.now(), status, fault: applied, bytes: raw.length, systemBytes: sys.length, toolsBytes: JSON.stringify(body.tools ?? []).length, messages: (body.messages ?? []).length, toolResultBytes, step: d.step, tool: d.tool?.name ?? null, editMode: editModeOf(body.tools), roles }) + "\n");
		if (process.env.BENCH_DUMP) fs.writeFileSync(`${metricsPath}.${i}.json`, raw);
		const est = Math.round(raw.length / 4);
		if (fault === "small-context" && est > SMALL_CONTEXT) {
			applied = fault; rec(400);
			res.writeHead(400, { "content-type": "application/json" });
			res.end(JSON.stringify({ error: { message: `This model's maximum context length is ${SMALL_CONTEXT} tokens. However, your messages resulted in ${est} tokens. Please reduce the length of the messages.`, type: "invalid_request_error", code: "context_length_exceeded" } }));
			return;
		}
		if (fault === "rate-limit" && i === 1) {
			applied = fault; rec(429);
			res.writeHead(429, { "content-type": "application/json", "retry-after": "2" });
			res.end(JSON.stringify({ error: { message: "Rate limit reached for requests. Please try again in 2s.", type: "requests", code: "rate_limit_exceeded" } }));
			return;
		}
		if ((fault === "server-error" && i === 2) || (fault === "error-burst" && i >= 2 && i <= 4)) {
			applied = fault; rec(503);
			res.writeHead(503, { "content-type": "application/json" });
			res.end(JSON.stringify({ error: { message: "upstream overloaded", type: "server_error" } }));
			return;
		}
		if (fault === "rate-limit-body" && i === 1) {
			applied = fault; rec(429);
			res.writeHead(429, { "content-type": "application/json" });
			res.end(JSON.stringify({ error: { message: "Rate limit reached for requests. Please try again in 3s.", type: "requests", code: "rate_limit_exceeded" } }));
			return;
		}
		if (fault === "empty-reply" && i === 2) {
			applied = fault; rec(200);
			res.writeHead(200, { "content-type": "text/event-stream" });
			chunk(res, { role: "assistant", content: "" });
			chunk(res, {}, "stop");
			res.end("data: [DONE]\n\n");
			return;
		}
		res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache" });
		chunk(res, { role: "assistant", content: "" });
		for (const w of d.text.split(" ")) { chunk(res, { content: w + " " }); await sleep(5); }
		if (fault === "stream-drop" && i === 2) {
			applied = fault; rec(200);
			await sleep(20);
			res.socket.destroy();
			return;
		}
		if (fault === "bad-args" && i === 2 && d.tool) applied = fault;
		rec(200);
		if (d.tool) {
			let args = JSON.stringify(d.tool.args);
			if (applied === "bad-args") args = args.slice(0, Math.floor(args.length / 2));
			chunk(res, { tool_calls: [{ index: 0, id: `call_${i}`, type: "function", function: { name: d.tool.name, arguments: args } }] });
			chunk(res, {}, "tool_calls");
		} else chunk(res, {}, "stop");
		res.write(`data: ${JSON.stringify({ id: "b", object: "chat.completion.chunk", created: 0, model: "bench", choices: [], usage: { prompt_tokens: Math.round(raw.length / 4), completion_tokens: 20, total_tokens: Math.round(raw.length / 4) + 20 } })}\n\n`);
		res.end("data: [DONE]\n\n");
	});
}).listen(port, "127.0.0.1", () => console.error(`bench model on ${port} fault=${fault}`));
