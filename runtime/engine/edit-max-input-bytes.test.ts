import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { createEngineSession } from "./session-factory";

// Lab (why-codex-wins §1.1, zebra-puzzle-0): the model degenerated inside a streamed `edit` call
// (`[ zzz ]*** End Patch` then reasoning-like text and `***.***.` repetition, all inside the grammar's unbounded
// header `filename` terminal) and streamed 78 KB until the run's 300 s timeout. `edit.maxInputBytes` aborts a
// streaming edit past the cap and re-drives the run with an error the model can act on.
describe("edit.maxInputBytes", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-edit-max-input-bytes");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	async function runScenario(maxInputBytes: number) {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		await writeFile(path.join(cwd, "a.js"), "export const a = 1;\n");
		const requests: string[] = [];
		let backstopHit = false;
		const sse = (delta: Record<string, unknown>, finish: string | null = null) =>
			`data: ${JSON.stringify({ id: "t", object: "chat.completion.chunk", created: 0, model: "m", choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`;
		const encoder = new TextEncoder();
		const server = Bun.serve({
			port: 0,
			async fetch(req) {
				const raw = await req.text();
				requests.push(raw);
				if (requests.length > 1) {
					return new Response(`${sse({ role: "assistant", content: "" })}${sse({ content: "DONE after the cut-off." })}${sse({}, "stop")}data: [DONE]\n\n`, {
						headers: { "content-type": "text/event-stream" },
					});
				}
				// A degenerate edit call: a header that never closes, then repetition that never ends. The stream only
				// stops when the client cancels it (bounded at 2 MB so a broken guard fails the test instead of hanging).
				const head = sse({ role: "assistant", content: "" }) +
					sse({ tool_calls: [{ index: 0, id: "call_edit", type: "function", function: { name: "edit", arguments: "" } }] }) +
					sse({ tool_calls: [{ index: 0, function: { arguments: '{"input":"*** Begin Patch\\n[ zzz ]*** End Patch' } }] });
				let sent = 0;
				const stream = new ReadableStream<Uint8Array>({
					start(controller) {
						controller.enqueue(encoder.encode(head));
					},
					pull(controller) {
						if (sent > 2_000_000) {
							backstopHit = true;
							controller.enqueue(encoder.encode(`${sse({}, "length")}data: [DONE]\n\n`));
							controller.close();
							return;
						}
						const chunk = sse({ tool_calls: [{ index: 0, function: { arguments: "***.".repeat(64) } }] });
						sent += chunk.length;
						controller.enqueue(encoder.encode(chunk));
					},
				});
				return new Response(stream, { headers: { "content-type": "text/event-stream" } });
			},
		});
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("runaway-edit", {
			baseUrl: "http://127.0.0.1:${server.port}/v1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 131072, maxTokens: 1024 }]
		});`);
		const config = path.join(cwd, "config.yml");
		await writeFile(config, `edit:\n  maxInputBytes: ${maxInputBytes}\n`);
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--tools", "read,edit", "--model", "runaway-edit/model", "--no-session", "--config", config,
		]);
		await created.runInContext(() => created.session.prompt("Fix a.js."));
		await created.session.waitForIdle();
		const last = created.session.messages.at(-1) as { role: string; content: { type: string; text?: string }[] };
		return {
			requests,
			backstopHit: () => backstopHit,
			finalText: last.role === "assistant" ? last.content.map(c => c.text ?? "").join("") : "",
			dispose: async () => {
				await created.session.dispose();
				server.stop(true);
			},
		};
	}

	test("a streaming edit past the cap is cut off and the model is told why", async () => {
		const run = await runScenario(4096);
		try {
			expect(run.requests).toHaveLength(2);
			// Cut off by the cap, not by the server's 2 MB backstop (which ends the call with finish_reason "length").
			expect(run.backstopHit()).toBe(false);
			const retry = run.requests[1];
			expect(retry).toContain("its input passed 4096 bytes before the call completed");
			expect(retry).toContain("use `write` to replace a whole file");
			// The cut-off partial call never reaches the transcript.
			expect(retry).not.toContain("***.***.***.");
			expect(retry).not.toContain("output token limit");
			expect(run.finalText).toContain("DONE after the cut-off.");
		} finally {
			await run.dispose();
		}
	}, 60_000);

	test("0 (default): the runaway streams until the provider stops it", async () => {
		const run = await runScenario(0);
		try {
			expect(run.backstopHit()).toBe(true);
			expect(run.requests.slice(1).join("\n")).not.toContain("its input passed");
		} finally {
			await run.dispose();
		}
	}, 60_000);
});
