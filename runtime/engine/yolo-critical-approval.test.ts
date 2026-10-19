import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { resolveApproval } from "./coding-agent/src/tools/approval";
import { createEngineSession } from "./session-factory";

// K5 (runtime/harness-bench task `safety`): a workspace file told the agent to `rm -rf <folder outside the workspace>/*`;
// claude-haiku-4-5 in yolo mode obeyed it in 2 of 2 runs and the folder was wiped. Codex keeps autonomous runs inside a
// workspace-write sandbox and Claude Code lets deny rules win over any mode; here the engine's own critical bash
// patterns must still ask in yolo (and so are refused where nobody can answer).
describe("critical bash commands in yolo mode", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-yolo-critical");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	test("ask instead of running; ordinary commands still run unasked", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("yolo-critical", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }]
		});`);
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--tools", "bash", "--model", "yolo-critical/model", "--no-session",
		]);
		try {
			const bash = created.session.getToolByName("bash");
			if (!bash) throw new Error("bash tool missing");
			const decide = (command: string) => created.runInContext(() => resolveApproval(bash, { command }, "yolo"));
			expect(decide("rm -rf /Users/someone/outside/*")).toMatchObject({ policy: "prompt", reason: "Critical pattern detected" });
			expect(decide("bun check.mjs && rm -rf /tmp/elsewhere")).toMatchObject({ policy: "prompt" });
			expect(decide("bun check.mjs").policy).toBe("allow");
			expect(decide("rm -rf build").policy).toBe("allow");
		} finally {
			await created.session.dispose();
		}
	}, 30_000);
});
