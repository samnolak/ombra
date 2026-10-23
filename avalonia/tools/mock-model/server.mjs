// Stand-in OpenAI-compatible chat-completions server for exercising real omp end to end
// when the real local inference server (qwen-local/flash-next-w4a16) is not reachable.
// It is NOT a model: replies are scripted from the last user message.
//
//   node server.mjs [port]          (bun works too)
//
// Scripts (keyword anywhere in the last user message):
//   LONGTASK <minutes> [id=<name>]  agent loop: ~10 s of streamed text plus a `read` tool call per turn until
//                       <minutes> have passed since the first turn, then a final answer (FINAL-MARKER).
//                       Reads notes-0.md … notes-9.md in turn (tools/stand-in-env/setup.sh creates them).
//   BASHCALL            one `bash` tool call (exec tier: needs approval unless approval mode is yolo)
//   TOOLCALL            one `read` tool call, then a final answer
//   BIGREPLY <kib>      a single reply of <kib> KiB (drives omp's rpc_chunk path when > 1 MiB)
//   BROWSERCALL <url>   one `eval` tool call running `browser.open({url})` (exec tier), then a final answer that says
//                       whether the system prompt carried the Ombra browser note (NOTE-SEEN=yes|no)
//   SLOW                a long reply streamed slowly (for abort tests)
//   SLOWSHORT           a few seconds of streaming (for messages sent while the agent works)
//   SUBAGENT            one `task` tool call spawning a subagent that runs `LONGTASK 3 id=sub` (Stop/Message tests)
//   BGJOB               one background `bash` call (async) whose `read` waits on input that never comes (background
//                       jobs get no terminal and a closed stdin, so `read -p` alone would end at once and print nothing)
//   PLANCALL            in plan mode: writes local://PLAN.md, then proposes it with a write to xd://propose
//   WRITECALL           one `write` tool call creating marks-demo.py (a diff card in the conversation), then a final answer
//   TERMREAD            one `terminal_read` call (the Ombra's host tool: the user's terminal tab), then a final answer
//                       echoing what it returned (TERMREAD done. RESULT=…)
//   ASKCALL             one `ask` tool call with two questions (one choice with a recommended option; a multi-select
//                       with a preview), then a final answer echoing the tool's result (ASKCALL done. RESULT=…)
//   TODOCALL            one `todo` init call writing a three-task plan (phase "Smoke"), then a final answer
//   BTWQ <word> [SLOW]  a /btw side question (omp's <btw> prompt): "BTW-ANSWER <word>: …" streamed, without tools;
//                       with SLOW, ~40 s of streaming (to cancel). Checked first: the side turn carries the conversation.
//   MDREPLY             a Markdown reply: two paragraphs (the first wraps), a list, a code block and a last paragraph
//   anything else       a short streamed reply
//
// Env: MOCK_TOKEN_DELAY_MS (default 15), MOCK_LOG=1 to log requests to stderr.
import http from "node:http";

const port = Number(process.argv[2] ?? process.env.MOCK_PORT ?? 18080);
const tokenDelay = Number(process.env.MOCK_TOKEN_DELAY_MS ?? 15);
const log = (...a) => process.env.MOCK_LOG && console.error(new Date().toISOString(), ...a);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const conversations = new Map(); // duration plus optional run ID -> first-turn timestamp

function textOf(content) {
	if (typeof content === "string") return content;
	if (Array.isArray(content)) return content.map(p => (typeof p === "string" ? p : (p?.text ?? ""))).join("");
	return "";
}

function plan(body) {
	const msgs = body.messages ?? [];
	const users = msgs.filter(m => m.role === "user");
	const lastUser = textOf(users.at(-1)?.content);
	const firstUser = textOf(users[0]?.content);
	const afterLastUser = msgs.slice(msgs.findLastIndex(m => m.role === "user") + 1);
	const toolResults = afterLastUser.filter(m => m.role === "tool").length;
	log("last user:", JSON.stringify(lastUser.slice(0, 160)), "…", JSON.stringify(lastUser.slice(-120)), "tools after it:", toolResults);
	const hasTools = Array.isArray(body.tools) && body.tools.length > 0;
	const toolNames = new Set((body.tools ?? []).map(t => t.function?.name));
	log("tools:", [...toolNames].join(","));

	// The task survives omp's own injected user messages and context compaction: look for it in any message
	// (the handoff reply below restates it) and key its clock on the task text alone.
	const allText = msgs.map(x => textOf(x.content)).join("\n");
	const btw = /BTWQ\s+(\S+)/.exec(lastUser);
	if (btw && /<btw>/.test(lastUser)) {
		if (/BTWQ\s+\S+\s+SLOW/.test(lastUser)) {
			return { text: `BTW-ANSWER ${btw[1]}: ` + Array.from({ length: 800 }, (_, i) => `aside${i} `).join(""), delay: 50 };
		}
		return { text: `BTW-ANSWER ${btw[1]}: a side answer from the conversation so far.`, delay: 40 };
	}
	if (/Write a handoff document/.test(lastUser)) {
		const t = /LONGTASK\s+\d+(?:\.\d+)?(?:\s+id=[\w-]+)?/.exec(allText);
		return { text: `Handoff summary. ${t ? `The task is ${t[0]} minutes; keep going.` : "Nothing to hand off."}`, delay: 1 };
	}
	const parentOfSubagent = users.some(u => /SUBAGENT/.test(textOf(u.content)));
	if (/SUBAGENT/.test(lastUser) && hasTools && toolNames.has("task")) {
		if (toolResults > 0) return { text: "SUBAGENT done." };
		const props = body.tools.find(t => t.function?.name === "task")?.function?.parameters?.properties ?? {};
		const item = { name: "SmokeWorker", agent: "task", task: "LONGTASK 3 id=sub", solutionSpace: "scripted stand-in" };
		const args = props.tasks ? { context: "Stand-in smoke subagent.", tasks: [item] } : item;
		return { text: "Starting a helper.", tool: { name: "task", args } };
	}
	if (/BGJOB/.test(lastUser) && hasTools && toolNames.has("bash")) {
		if (toolResults > 0) return { text: "BGJOB started." };
		const command = "echo started; sleep 600 | { printf 'Your name? '; read n; echo \"hello $n\"; }";
		return { text: "Starting a background job.", tool: { name: "bash", args: { command, async: true } } };
	}
	if (/PLANCALL/.test(lastUser) && hasTools && toolNames.has("write")) {
		if (toolResults === 0) {
			const content = "# Smoke plan\n\n1. Read README.md.\n2. Write a summary.\n";
			return { text: "Drafting the plan.", tool: { name: "write", args: { path: "local://PLAN.md", content } } };
		}
		if (toolResults === 1) return { text: "Proposing the plan.", tool: { name: "write", args: { path: "xd://propose", content: "smoke" } } };
		return { text: "Waiting for approval.", delay: 200 };
	}
	if (/ASKCALL/.test(lastUser) && hasTools && toolNames.has("ask")) {
		if (toolResults === 0) {
			const questions = [
				{ id: "color", question: "Which color should the banner use?", header: "Color", recommended: 0,
					options: [{ label: "Teal", description: "matches the logo" }, { label: "Amber" }, { label: "Slate" }] },
				{ id: "extras", question: "Which extras should ship with it?", header: "Extras", multi: true,
					options: [{ label: "Tests" }, { label: "Docs", preview: "README.md: a Banner section" }, { label: "Changelog" }] },
			];
			return { text: "Two questions first.", tool: { name: "ask", args: { questions } } };
		}
		const result = textOf(afterLastUser.filter(x => x.role === "tool").at(-1)?.content);
		return { text: `ASKCALL done. RESULT=${result.replace(/\s+/g, " ").slice(0, 400)}` };
	}
	if (/TODOCALL/.test(lastUser) && hasTools && toolNames.has("todo")) {
		if (toolResults === 0) {
			const list = [{ phase: "Smoke", items: ["Read README.md", "Write the summary", "Ship it"] }];
			return { text: "Writing the plan.", tool: { name: "todo", args: { op: "init", list } } };
		}
		return { text: "TODOCALL done." };
	}
	let m = /LONGTASK\s+(\d+(?:\.\d+)?)(?:\s+id=[\w-]+)?/.exec(allText);
	if (m && !parentOfSubagent && hasTools && toolNames.has("read")) {
		// read is auto-approved in every approval mode, so the loop never waits on a dialog.
		const key = m[0];
		if (!conversations.has(key)) conversations.set(key, Date.now());
		const elapsedMin = (Date.now() - conversations.get(key)) / 60000;
		const step = Math.floor(msgs.filter(x => x.role === "tool").length) + 1;
		if (elapsedMin < Number(m[1])) {
			const filler = Array.from({ length: 120 }, (_, i) => `s${step}w${i}`).join(" ");
			return {
				text: `Step ${step} at ${elapsedMin.toFixed(2)} of ${m[1]} minutes. ${filler} END-STEP-${step}.`,
				delay: 50,
				// distinct files: omp's loop detector interrupts after 5 identical consecutive calls
				tool: { name: "read", args: { path: `notes-${step % 10}.md` } },
			};
		}
		conversations.delete(key);
		return { text: `LONGTASK finished after ${step - 1} tool calls and ${elapsedMin.toFixed(2)} minutes. FINAL-MARKER` };
	}
	if (/SGRCALL/.test(lastUser) && hasTools && toolNames.has("bash")) {
		if (toolResults === 0) return { text: "Printing styled tool output.", tool: { name: "bash", args: { command: "printf '\\033[1;31mSGR red text\\033[0m\\n'" } } };
		return { text: "SGRCALL done." };
	}
	if (/BASHCALL/.test(lastUser) && hasTools && toolNames.has("bash")) {
		if (toolResults === 0) return { text: "Running a command.", tool: { name: "bash", args: { command: "echo hi" } } };
		return { text: "BASHCALL done." };
	}
	if (/TERMREAD/.test(lastUser) && hasTools && toolNames.has("terminal_read")) {
		if (toolResults === 0) return { text: "Reading your terminal.", tool: { name: "terminal_read", args: {} } };
		const result = textOf(afterLastUser.filter(x => x.role === "tool").at(-1)?.content);
		return { text: `TERMREAD done. RESULT=${result.slice(-600)}` };
	}
	if (/TOOLCALL/.test(lastUser) && hasTools && toolNames.has("read")) {
		if (toolResults === 0) return { text: "Reading the file first.", tool: { name: "read", args: { path: "README.md" } } };
		return { text: "I read the file. It exists and has content. TOOLCALL done." };
	}
	if (/WRITECALL/.test(lastUser) && hasTools && toolNames.has("write")) {
		if (toolResults > 0) return { text: "WRITECALL done." };
		const content = "import os\n\n\ndef total(items):\n    result = 0\n    for item in items:\n        result += item\n    return result\n\n\nprint(total([1, 2, 3]))\n";
		return { text: "Writing the file.", tool: { name: "write", args: { path: "marks-demo.py", content } } };
	}
	m = /BROWSERCALL\s+(\S+)/.exec(lastUser);
	if (m && hasTools && toolNames.has("eval")) {
		const system = msgs.filter(x => x.role === "system" || x.role === "developer").map(x => textOf(x.content)).join("\n");
		const seen = system.includes("the Ombra's built-in browser") ? "yes" : "no";
		if (toolResults === 0) {
			const code = `const tab = await browser.open({ url: ${JSON.stringify(m[1])}, persist: true });\nprint(await tab.url());`;
			return { text: `Opening the page. NOTE-SEEN=${seen}`, tool: { name: "eval", args: { language: "js", code } } };
		}
		const result = textOf(afterLastUser.filter(x => x.role === "tool").at(-1)?.content);
		return { text: `BROWSERCALL done. NOTE-SEEN=${seen} RESULT=${result.slice(0, 400)}` };
	}
	m = /BIGREPLY\s+(\d+)/.exec(lastUser);
	if (m) {
		// Pseudo-random words: omp's stall detector aborts replies that repeat an exact cycle.
		const words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "ünïcødé", "漢字"];
		let seed = 12345, out = "", line = 0;
		while (out.length < Number(m[1]) * 1024) {
			let l = `${++line}:`;
			for (let w = 0; w < 10; w++) { seed = (seed * 1103515245 + 12345) % 2147483648; l += " " + words[seed % words.length]; }
			out += l + "\n";
		}
		return { text: out, chunkChars: 16384, delay: 1 };
	}
	if (/MDREPLY/.test(lastUser)) {
		return {
			text: "First paragraph of the reply, long enough to wrap onto a second line in the conversation column, so a drag goes down through it.\n\n"
				+ "Second paragraph with `inline code` and more words.\n\n- item one\n- item two\n\n```python\ndef f():\n    return 1\n```\n\nLast paragraph.",
		};
	}
	if (/SLOWSHORT/.test(lastUser)) {
		return { text: Array.from({ length: 150 }, (_, i) => `word${i} `).join(""), delay: 30 };
	}
	if (/SLOW/.test(lastUser)) {
		return { text: Array.from({ length: 2000 }, (_, i) => `word${i} `).join(""), delay: 50 };
	}
	return { text: `Stand-in model reply to: ${lastUser.slice(0, 80)}. This is streamed token by token.` };
}

function chunk(id, model, delta, finish = null) {
	return `data: ${JSON.stringify({
		id,
		object: "chat.completion.chunk",
		created: Math.floor(Date.now() / 1000),
		model,
		choices: [{ index: 0, delta, finish_reason: finish }],
	})}\n\n`;
}

async function streamReply(req, res, body) {
	const p = plan(body);
	const id = `chatcmpl-${Date.now()}`;
	const model = body.model ?? "flash-next-w4a16";
	let closed = false;
	res.on("close", () => (closed = !res.writableEnded));
	res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache", connection: "keep-alive" });
	res.write(chunk(id, model, { role: "assistant", content: "" }));
	const size = p.chunkChars ?? 6;
	const delay = p.delay ?? tokenDelay;
	for (let i = 0; i < p.text.length && !closed; i += size) {
		res.write(chunk(id, model, { content: p.text.slice(i, i + size) }));
		if (delay) await sleep(delay);
	}
	if (closed) return log("client closed stream");
	if (p.tool) {
		res.write(
			chunk(id, model, {
				tool_calls: [{ index: 0, id: `call_${Date.now()}`, type: "function", function: { name: p.tool.name, arguments: "" } }],
			}),
		);
		res.write(chunk(id, model, { tool_calls: [{ index: 0, function: { arguments: JSON.stringify(p.tool.args) } }] }));
		res.write(chunk(id, model, {}, "tool_calls"));
	} else {
		res.write(chunk(id, model, {}, "stop"));
	}
	res.write(
		`data: ${JSON.stringify({ id, object: "chat.completion.chunk", model, choices: [], usage: { prompt_tokens: 100, completion_tokens: Math.ceil(p.text.length / 4), total_tokens: 100 + Math.ceil(p.text.length / 4) } })}\n\n`,
	);
	res.end("data: [DONE]\n\n");
}

http
	.createServer((req, res) => {
		let raw = "";
		req.on("data", d => (raw += d));
		req.on("end", async () => {
			log(req.method, req.url, raw.length);
			if (req.method === "GET" && req.url.endsWith("/models")) {
				res.writeHead(200, { "content-type": "application/json", "x-ombra-stand-in": "1" });
				return res.end(JSON.stringify({ object: "list", data: [{ id: "flash-next-w4a16", object: "model" }] }));
			}
			if (req.method === "POST" && req.url.endsWith("/chat/completions")) {
				try {
					const body = JSON.parse(raw);
					if (body.stream === false) {
						const p = plan(body);
						res.writeHead(200, { "content-type": "application/json" });
						return res.end(
							JSON.stringify({
								id: "x",
								object: "chat.completion",
								model: body.model,
								choices: [{ index: 0, message: { role: "assistant", content: p.text }, finish_reason: "stop" }],
							}),
						);
					}
					return await streamReply(req, res, body);
				} catch (e) {
					log("error", e);
					res.writeHead(400);
					return res.end(String(e));
				}
			}
			// An MCP server that wants a sign-in (the feature smoke's "needs-sign-in" server): every request is refused
			if (req.url.startsWith("/mcp-sign-in")) {
				res.writeHead(401, { "content-type": "application/json", "www-authenticate": 'Bearer realm="stand-in"' });
				return res.end(JSON.stringify({ error: "invalid_token", error_description: "Sign in first" }));
			}
			res.writeHead(404);
			res.end();
		});
	})
	.listen(port, "127.0.0.1", () => console.error(`mock model listening on http://127.0.0.1:${port}/v1`));
