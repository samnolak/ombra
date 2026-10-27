import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { createEngineSession } from "./session-factory";

// Lab (why-codex-wins §1.3): `eval` advertised `py` while the Python backend could not start; the model called it,
// got "Python backend is unavailable", and in 3 of 10 SWE runs gave up. With `eval.probePython` the session probes
// Python once at start and, when it cannot start, the eval tool offers only `js` and says why in one line.
describe("eval.probePython", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-eval-probe-python");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	async function evalTool(settings: string) {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("probe-py", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }]
		});`);
		const config = path.join(cwd, "config.yml");
		await writeFile(config, settings);
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--tools", "eval", "--model", "probe-py/model", "--no-session", "--config", config,
		]);
		const tool = created.session.getToolByName("eval");
		if (!tool) throw new Error("eval tool missing");
		const params: unknown = tool.parameters;
		const toJsonSchema =
			(typeof params === "function" || typeof params === "object") && params !== null && "toJsonSchema" in params
				? params.toJsonSchema
				: undefined;
		if (typeof toJsonSchema !== "function") throw new Error("eval parameters carry no JSON schema");
		const schema = JSON.stringify(toJsonSchema.call(params));
		return { created, tool, schema };
	}

	test("an interpreter that cannot start drops py from the schema and says why", async () => {
		const { created, tool, schema } = await evalTool(
			"eval:\n  probePython: true\npython:\n  interpreter: /nonexistent/ombra-python3\n",
		);
		try {
			expect(schema).toContain('"js"');
			expect(schema).not.toContain('"py"');
			expect(tool.description).toContain("Python eval cannot start in this session (");
			expect(tool.description).toContain("only `js` runs.");
			expect(tool.description).not.toContain("Python: plain Python subprocess");
			expect(tool.summary).toContain("JavaScript");
			expect(tool.summary).not.toContain("Python");
			// A model that sends `py` anyway gets the reason, not "disabled".
			const result = await created.runInContext(() =>
				tool.execute("call-py", { language: "py", code: "print(1)" }).then(
					() => "",
					(error: unknown) => (error instanceof Error ? error.message : String(error)),
				),
			);
			expect(result).toContain("Python backend cannot start in this session");
			expect(result).toContain('Pass language: "js"');
		} finally {
			await created.session.dispose();
		}
	}, 30_000);

	test("off (default): py stays advertised even when it cannot start", async () => {
		const { created, tool, schema } = await evalTool("python:\n  interpreter: /nonexistent/ombra-python3\n");
		try {
			expect(schema).toContain('"py"');
			expect(tool.description).not.toContain("Python eval cannot start");
		} finally {
			await created.session.dispose();
		}
	}, 30_000);

	const python = Bun.which("python3");
	test.skipIf(!python)("a working interpreter keeps py", async () => {
		const { created, tool, schema } = await evalTool(`eval:\n  probePython: true\npython:\n  interpreter: ${python}\n`);
		try {
			expect(schema).toContain('"py"');
			expect(schema).toContain('"js"');
			expect(tool.description).not.toContain("Python eval cannot start");
		} finally {
			await created.session.dispose();
		}
	}, 30_000);
});
