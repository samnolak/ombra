import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { createEngineSession } from "./session-factory";

// K5 (runtime/harness-bench polyglot.mjs): a 100–300-line file came back as a structural outline with bodies
// elided, and the agent re-read it range by range. A file one plain read returns whole is now returned whole.
describe("read of a file within one page", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-read-whole-page");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	test("a 200-line test file is returned verbatim; a 400-line one still as an outline", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		const spec = (cases: number) =>
			`import { score } from "./bowling";\n\ndescribe("Bowling", () => {\n${Array.from({ length: cases }, (_, i) =>
				`  test("case ${i}", () => {\n    const game = [];\n    for (let r = 0; r < 20; r++) game.push(${i % 10});\n    const total = score(game);\n    const expected = ${i * 20};\n    expect(total).toEqual(expected);\n    expect(game.length).toBe(20);\n  });\n`).join("")}});\n`;
		await writeFile(path.join(cwd, "short.spec.js"), spec(22)); // ~200 lines
		await writeFile(path.join(cwd, "long.spec.js"), spec(45)); // ~400 lines
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("read-page", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }]
		});`);
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--tools", "read", "--model", "read-page/model", "--no-session",
		]);
		try {
			const read = created.session.getToolByName("read");
			if (!read) throw new Error("read tool missing");
			const text = async (file: string) => {
				const result = await created.runInContext(() => read.execute(`call-${file}`, { path: file }));
				return result.content.map(c => ("text" in c ? c.text : "")).join("");
			};
			const short = await text("short.spec.js");
			expect(short).not.toContain("elided");
			expect(short).toContain("expect(game.length).toBe(20);");
			expect(short).toContain("const expected = 420;");
			expect(await text("long.spec.js")).toContain("elided");
		} finally {
			await created.session.dispose();
		}
	}, 30_000);
});
