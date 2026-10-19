import { afterAll, afterEach, expect, test } from "bun:test";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";

// The real engine host (host.ts --mode engine) with a local OpenAI-compatible model that calls `bash`: the tool
// approval reaches the client as a typed request, and a denial's reason reaches the model in the tool result.
const engineRoot = process.env.OMBRA_ENGINE_ROOT;
if (!engineRoot) throw new Error("Set OMBRA_ENGINE_ROOT to runtime/engine with the pinned omp/node_modules linked");
if (Bun.version !== "1.4.2") throw new Error(`Use the pack's pinned Bun 1.4.2, not ${Bun.version}`);

type Frame = Record<string, any>;
const runRoot = resolve(import.meta.dir, "../../.temp/ombra-tests", `run-${process.pid}`, "structured-approval");
const cleanups: Array<() => Promise<void> | void> = [];
afterEach(async () => { for (const cleanup of cleanups.splice(0).reverse()) await cleanup(); });
afterAll(() => rm(runRoot, { recursive: true, force: true }));

class Host {
	readonly frames: Frame[] = [];
	readonly exited: Promise<number | null>;
	readonly #process;
	#stderr = "";
	#sequence = 0;
	#changed = new Set<() => void>();
	constructor(env: NodeJS.ProcessEnv, cwd: string) {
		this.#process = spawn(process.execPath, [join(resolve(engineRoot!), "host.ts"), "--mode", "engine"], { cwd, env, stdio: ["pipe", "pipe", "pipe"] });
		const exited = Promise.withResolvers<number | null>();
		this.#process.once("exit", exited.resolve);
		this.exited = exited.promise;
		this.#process.stderr.on("data", data => { this.#stderr = (this.#stderr + data).slice(-16384); });
		createInterface({ input: this.#process.stdout }).on("line", line => {
			this.frames.push(JSON.parse(line));
			for (const wake of this.#changed) wake();
		});
	}
	/** The first frame from index `from` on that `predicate` accepts. */
	wait(predicate: (frame: Frame) => boolean, what: string, timeoutMs = 30_000, from = 0): Promise<Frame> {
		const pending = Promise.withResolvers<Frame>();
		const finish = () => {
			const frame = this.frames.slice(from).find(predicate);
			if (frame) { clearTimeout(timer); this.#changed.delete(finish); pending.resolve(frame); }
		};
		// Integration deadline only: success is driven by an actual pipe frame, never a guessed delay.
		const timer = setTimeout(() => { this.#changed.delete(finish); pending.reject(new Error(`Timed out waiting for ${what}: ${this.#stderr}`)); }, timeoutMs);
		this.#changed.add(finish);
		finish();
		return pending.promise;
	}
	waitSession(key: string, predicate: (frame: Frame) => boolean, what: string, from = 0): Promise<Frame> {
		return this.wait(frame => frame.type === "engine_session_frame" && frame.sessionKey === key && predicate(frame.frame), what, 30_000, from)
			.then(frame => frame.frame);
	}
	write(frame: object): void { this.#process.stdin.write(`${JSON.stringify(frame)}\n`); }
	request(type: string, fields: object = {}): Promise<Frame> {
		const id = `host-${++this.#sequence}`;
		this.write({ ...fields, type, id });
		return this.wait(frame => frame.type === "response" && frame.id === id, type, 60_000);
	}
	command(key: string, type: string, fields: object = {}): Promise<Frame> {
		const id = `cmd-${++this.#sequence}`;
		this.sendSession(key, { ...fields, type, id });
		return this.waitSession(key, frame => frame.type === "response" && frame.id === id, type);
	}
	sendSession(key: string, frame: object): void { this.write({ type: "engine_session_command", sessionKey: key, frame }); }
	async stop(): Promise<void> {
		if (this.#process.exitCode !== null || this.#process.signalCode !== null) return;
		this.#process.kill("SIGKILL");
		await this.exited;
	}
}

/** An OpenAI chat-completions stand-in: a prompt naming a command makes it call `bash`; a tool result ends the turn. */
function modelServer() {
	const toolResults: string[] = [];
	const sse = (delta: object, finish: string | null = null) =>
		`data: ${JSON.stringify({ id: "c", object: "chat.completion.chunk", created: 0, model: "m", choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`;
	const server = Bun.serve({
		hostname: "127.0.0.1",
		port: 0,
		async fetch(request) {
			if (!new URL(request.url).pathname.endsWith("/chat/completions")) return new Response("not found", { status: 404 });
			const body = await request.json() as { messages: Array<{ role: string; content: unknown }> };
			const last = body.messages.at(-1)!;
			const text = typeof last.content === "string" ? last.content
				: Array.isArray(last.content) ? last.content.map(part => (part as { text?: string }).text ?? "").join("") : "";
			let events: string;
			const command = /RUN (.+)$/m.exec(text)?.[1];
			const cwd = /CWD (.+)$/m.exec(text)?.[1];
			if (last.role === "tool") {
				toolResults.push(text);
				events = sse({ role: "assistant", content: "done" }) + sse({}, "stop");
			} else if (last.role === "user" && command) {
				events = sse({ role: "assistant", content: "" })
					+ sse({ tool_calls: [{ index: 0, id: `call_${toolResults.length}`, type: "function", function: { name: "bash", arguments: "" } }] })
					+ sse({ tool_calls: [{ index: 0, function: { arguments: JSON.stringify(cwd ? { command, cwd } : { command }) } }] })
					+ sse({}, "tool_calls");
			} else {
				events = sse({ role: "assistant", content: "title" }) + sse({}, "stop");
			}
			return new Response(`${events}data: [DONE]\n\n`, { headers: { "content-type": "text/event-stream" } });
		},
	});
	return { url: `http://127.0.0.1:${server.port}/v1`, toolResults, stop: () => server.stop(true) };
}

async function fixture() {
	await mkdir(runRoot, { recursive: true });
	const root = await mkdtemp(join(runRoot, "case-"));
	const agentDir = join(root, "agent");
	const project = join(root, "project");
	await mkdir(agentDir, { recursive: true });
	await mkdir(project, { recursive: true });
	const model = modelServer();
	cleanups.push(model.stop);
	const provider = join(root, "provider.ts");
	await writeFile(provider, `export default api => api.registerProvider("approval-probe", {
		baseUrl: ${JSON.stringify(model.url)}, apiKey: "unused-probe-key", api: "openai-completions",
		models: [{ id: "m", name: "m", reasoning: false, input: ["text"],
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }],
	});`);
	const env: NodeJS.ProcessEnv = { PATH: process.env.PATH, HOME: root, PI_CODING_AGENT_DIR: agentDir, PI_PROFILE: "", OMP_PROFILE: "",
		XDG_CONFIG_HOME: join(root, "config"), XDG_STATE_HOME: join(root, "state"), XDG_DATA_HOME: join(root, "data"), XDG_CACHE_HOME: join(root, "cache"),
		PI_TEST_SESSION_OWNERS_DIR: join(root, "owners"), PI_NOTIFICATIONS: "off" };
	const host = new Host(env, project);
	cleanups.push(() => host.stop());
	await host.wait(frame => frame.type === "ready", "ready");
	expect(await host.request("negotiate_protocol", { protocolVersion: 2 })).toMatchObject({ success: true });
	const key = "chat";
	expect(await host.request("engine_open", { sessionKey: key, cwd: project, arguments: [
		"--extension", provider, "--no-extensions", "--model", "approval-probe/m", "--approval-mode", "write", "--tools", "bash",
		"--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
	] })).toMatchObject({ success: true });
	return { host, key, model };
}

const isApproval = (frame: Frame) => frame.type === "extension_ui_request" && frame.method === "select" && frame.approval !== undefined;

test("a tool approval is a typed request; Deny carries the user's reason into the tool result", async () => {
	const { host, key, model } = await fixture();
	expect(await host.command(key, "prompt", { message: "RUN echo approval-probe-deny" })).toMatchObject({ success: true });
	const request = await host.waitSession(key, isApproval, "approval request");
	// The text prompt stays for hosts without structured approval.
	expect(request.title.split("\n")[0]).toBe("Allow tool: bash");
	expect(request.options).toEqual(["Approve", "Deny"]);
	expect(request.approval).toMatchObject({
		tool: "bash",
		risk: "exec",
		command: "echo approval-probe-deny",
		details: ["Command: echo approval-probe-deny"],
		scopes: ["once", "session", "always"],
	});
	expect(typeof request.approval.toolCallId).toBe("string");
	expect(request.approval.safetyChecks).toBeUndefined();
	host.sendSession(key, { type: "extension_ui_response", id: request.id, value: "Deny", reason: "use printf, not echo" });
	await host.waitSession(key, frame => frame.type === "prompt_result", "prompt result");
	expect(model.toolResults).toHaveLength(1);
	expect(model.toolResults[0]).toContain("Tool call denied by user: bash");
	expect(model.toolResults[0]).toContain('The user said (approval prompt): "use printf, not echo"');
}, 90_000);

test("Approve runs the call; a plain Deny without reason stays a plain denial", async () => {
	const { host, key, model } = await fixture();
	expect(await host.command(key, "prompt", { message: "RUN echo approval-probe-ok" })).toMatchObject({ success: true });
	const first = await host.waitSession(key, isApproval, "first approval");
	host.sendSession(key, { type: "extension_ui_response", id: first.id, value: "Approve" });
	await host.waitSession(key, frame => frame.type === "prompt_result", "first prompt result");
	expect(model.toolResults[0]).toContain("approval-probe-ok");

	const mark = host.frames.length;
	expect(await host.command(key, "prompt", { message: "RUN echo approval-probe-plain" })).toMatchObject({ success: true });
	const second = await host.waitSession(key, isApproval, "second approval", mark);
	host.sendSession(key, { type: "extension_ui_response", id: second.id, value: "Deny" });
	await host.waitSession(key, frame => frame.type === "prompt_result", "second prompt result", mark);
	expect(model.toolResults[1]).toContain("Tool call denied by user: bash");
	expect(model.toolResults[1]).not.toContain("The user said");
}, 90_000);

// ReviewLease P1/P2: what the engine insists a person answers offers only "once", and bash's cwd is in the request.
test("a critical command offers only once; a command in another folder names that folder", async () => {
	const { host, key, model } = await fixture();
	expect(await host.command(key, "prompt", { message: "RUN rm -rf /ombra-approval-probe-never-runs" })).toMatchObject({ success: true });
	const critical = await host.waitSession(key, isApproval, "critical approval");
	expect(critical.approval).toMatchObject({ tool: "bash", reason: "Critical pattern detected", scopes: ["once"] });
	host.sendSession(key, { type: "extension_ui_response", id: critical.id, value: "Deny" });
	await host.waitSession(key, frame => frame.type === "prompt_result", "critical prompt result");
	expect(model.toolResults[0]).toContain("Tool call denied by user: bash");

	const mark = host.frames.length;
	expect(await host.command(key, "prompt", { message: "RUN echo elsewhere\nCWD ../elsewhere" })).toMatchObject({ success: true });
	const elsewhere = await host.waitSession(key, isApproval, "cwd approval", mark);
	expect(elsewhere.approval).toMatchObject({ command: "echo elsewhere", cwd: "../elsewhere", details: ["Command: echo elsewhere", "Cwd: ../elsewhere"] });
	expect(elsewhere.title).toContain("\nCwd: ../elsewhere");
	host.sendSession(key, { type: "extension_ui_response", id: elsewhere.id, value: "Deny" });
	await host.waitSession(key, frame => frame.type === "prompt_result", "cwd prompt result", mark);
}, 90_000);
