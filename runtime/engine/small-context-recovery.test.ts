import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { createEngineSession, type EngineSessionResult } from "./session-factory";

// K5 (runtime/harness-bench, fault "small-context"): a local OpenAI-compatible server started with a shorter
// context than the model's configured window rejects the turn that carries one large tool result, naming its
// real limit. The engine must adopt that limit, elide the oversized result to an artifact and retry, instead of
// dead-ending with "Compaction freed too little context".
describe("provider-reported context window smaller than the configured one", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-small-context");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	// The scenario: a read of a large log overflows the server's real window; the scripted model then answers.
	async function runScenario(prepare?: (session: EngineSessionResult["session"]) => void) {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		const lines = Array.from({ length: 900 }, (_, i) => `log line ${i}: request rejected because the separator was ';'`);
		await writeFile(path.join(cwd, "input.log"), `${lines.join("\n")}\n`);

		const requests: { status: number; bytes: number }[] = [];
		let limitTokens = 0;
		const sse = (delta: Record<string, unknown>, finish: string | null = null) =>
			`data: ${JSON.stringify({ id: "t", object: "chat.completion.chunk", created: 0, model: "m", choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`;
		const server = Bun.serve({
			port: 0,
			async fetch(req) {
				const raw = await req.text();
				const body = JSON.parse(raw) as { messages: { role: string }[] };
				const estimate = Math.round(raw.length / 4);
				// The server's real window: comfortably above the bare prompt, far below prompt + the whole log.
				if (limitTokens === 0) limitTokens = Math.round(estimate * 1.6);
				if (estimate > limitTokens) {
					requests.push({ status: 400, bytes: raw.length });
					return Response.json(
						{
							error: {
								message: `This model's maximum context length is ${limitTokens} tokens. However, your messages resulted in ${estimate} tokens. Please reduce the length of the messages.`,
								type: "invalid_request_error",
								code: "context_length_exceeded",
							},
						},
						{ status: 400 },
					);
				}
				requests.push({ status: 200, bytes: raw.length });
				const toolResults = body.messages.filter(m => m.role === "tool").length;
				const reply =
					toolResults === 0
						? sse({ tool_calls: [{ index: 0, id: "call_read", type: "function", function: { name: "read", arguments: JSON.stringify({ path: "input.log" }) } }] }) +
							sse({}, "tool_calls")
						: sse({ content: "DONE after reading the log." }) + sse({}, "stop");
				return new Response(`${sse({ role: "assistant", content: "" })}${reply}data: [DONE]\n\n`, {
					headers: { "content-type": "text/event-stream" },
				});
			},
		});
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("small-context", {
			baseUrl: "http://127.0.0.1:${server.port}/v1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 131072, maxTokens: 1024 }]
		});`);
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--tools", "read", "--model", "small-context/model", "--no-session",
		]);
		prepare?.(created.session);
		await created.runInContext(() => created.session.prompt("Read input.log and tell me what is wrong."));
		await created.session.waitForIdle();
		const last = created.session.messages.at(-1) as { role: string; content: { type: string; text?: string }[] };
		return {
			created,
			requests,
			limitTokens: () => limitTokens,
			finalText: last.role === "assistant" ? last.content.map(c => c.text ?? "").join("") : "",
			dispose: async () => {
				await created.session.dispose();
				server.stop(true);
			},
		};
	}

	test("a single oversized read result is elided and the turn retried within the stated window", async () => {
		const run = await runScenario();
		try {
			// One rejection, then the retry fits: the read result went to an artifact, not into the request.
			expect(run.requests.filter(r => r.status === 400)).toHaveLength(1);
			expect(run.requests.at(-1)?.status).toBe(200);
			expect(run.created.session.model?.contextWindow).toBe(run.limitTokens());
			expect(run.finalText).toContain("DONE after reading the log.");
		} finally {
			await run.dispose();
		}
	}, 60_000);

	test("an auto-shake that throws still falls through to the rescue", async () => {
		const run = await runScenario(session => {
			const shake = session.shake.bind(session);
			// Only the protected auto-shake breaks; the unprotected rescue elide keeps working.
			session.shake = (mode, opts = {}) =>
				opts.config?.protectTokens ? Promise.reject(new Error("auto-shake broke")) : shake(mode, opts);
		});
		try {
			expect(run.requests.at(-1)?.status).toBe(200);
			expect(run.finalText).toContain("DONE after reading the log.");
		} finally {
			await run.dispose();
		}
	}, 60_000);

	test("the stated window survives re-selecting the same model", async () => {
		const run = await runScenario();
		try {
			const session = run.created.session;
			const registered = session.modelRegistry.find("small-context", "model");
			if (!registered) throw new Error("model not registered");
			expect(registered.contextWindow).toBe(131072);
			await run.created.runInContext(() => session.setModel(registered));
			expect(session.model?.contextWindow).toBe(run.limitTokens());
		} finally {
			await run.dispose();
		}
	}, 60_000);
});
