import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { toolWireSchema } from "@oh-my-pi/pi-ai";
import { createEngineSession } from "./session-factory";

// Lab (request-gap §1.4, fix 1): every one of combo's 291 test runs was a separate request right after an edit, and
// the one attempt to edit and test in one `eval` cell failed because `tool.edit("<patch>")` with a string threw
// "root: must be an object". `edit.run` lets an edit carry its check; the eval proxy maps a bare string to a
// single-input tool's one required field.
describe("edit.run", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-edit-run");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	const text = (result: { content: { type: string; text?: string }[] }) =>
		result.content.map(c => c.text ?? "").join("\n");

	async function scenario(settings: string, tools = "read,edit,bash") {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		await writeFile(path.join(cwd, "a.js"), "export const a = 1;\nexport const b = 2;\n");
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("edit-run", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }]
		});`);
		const config = path.join(cwd, "config.yml");
		await writeFile(config, `tools:\n  approvalMode: yolo\nedit:\n  mode: hashline\n${settings}`);
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--tools", tools, "--model", "edit-run/model", "--no-session", "--config", config,
		]);
		const tool = (name: string) => {
			const found = created.session.getToolByName(name);
			if (!found) throw new Error(`${name} tool missing`);
			return found;
		};
		const call = (name: string, id: string, args: unknown) =>
			created.runInContext(() => tool(name).execute(id, args as never)).then(
				result => ({ text: text(result), isError: result.isError === true }),
				(error: unknown) => ({ text: String(error), isError: true }),
			);
		const tag = async () => {
			const match = /\[a\.js#([0-9A-F]{4})\]/.exec((await call("read", "r", { path: "a.js" })).text);
			if (!match) throw new Error("no tag");
			return match[1];
		};
		const patch = (fileTag: string, body: string) =>
			`*** Begin Patch\n[a.js#${fileTag}]\nPUT 2.=2:\n+${body}\n*** End Patch\n`;
		return { cwd, created, tool, call, tag, patch, dispose: () => created.session.dispose() };
	}

	test("off (default): schema, grammar and description carry no run", async () => {
		const run = await scenario("");
		try {
			const edit = run.tool("edit");
			const schema = toolWireSchema(edit) as { properties: Record<string, unknown> };
			expect(Object.keys(schema.properties)).not.toContain("run");
			expect(edit.customFormat?.definition).not.toContain("*** Run: ");
			expect(edit.description).not.toContain("*** Run:");
		} finally {
			await run.dispose();
		}
	}, 30_000);

	test("on: the schema takes `run` and the freeform grammar a final `*** Run:` line", async () => {
		const run = await scenario("  run: true\n");
		try {
			const edit = run.tool("edit");
			const schema = toolWireSchema(edit) as { properties: Record<string, unknown>; required: string[] };
			expect(schema.properties.run).toMatchObject({ type: "string" });
			expect(schema.required).not.toContain("run");
			expect(edit.customFormat?.definition).toContain('end_patch: "*** End Patch" (LF "*** Run: "');
			expect(edit.description).toContain("*** Run: <command>");
		} finally {
			await run.dispose();
		}
	}, 30_000);

	test("on: a clean edit runs the `run` field and appends its output", async () => {
		const run = await scenario("  run: true\n");
		try {
			const result = await run.call("edit", "e1", {
				input: run.patch(await run.tag(), "export const b = 20;"),
				run: "grep -c 20 a.js",
			});
			expect(result.isError).toBe(false);
			expect(result.text).toContain("Run `grep -c 20 a.js`:\n1");
			expect(await readFile(path.join(run.cwd, "a.js"), "utf8")).toBe("export const a = 1;\nexport const b = 20;\n");
		} finally {
			await run.dispose();
		}
	}, 30_000);

	test("on: the freeform `*** Run:` line runs after the patch and is not applied as patch text", async () => {
		const run = await scenario("  run: true\n");
		try {
			const input = `${run.patch(await run.tag(), "export const b = 30;")}*** Run: cat a.js\n`;
			const result = await run.call("edit", "e1", { input });
			expect(result.isError).toBe(false);
			expect(result.text).toContain("Run `cat a.js`:\nexport const a = 1;\nexport const b = 30;");
		} finally {
			await run.dispose();
		}
	}, 30_000);

	test("on: a failing command is reported with its exit code; the edit stays applied", async () => {
		const run = await scenario("  run: true\n");
		try {
			const result = await run.call("edit", "e1", {
				input: run.patch(await run.tag(), "export const b = 40;"),
				run: "echo failing-check; exit 3",
			});
			expect(result.text).toContain("Run `echo failing-check; exit 3`:\nfailing-check");
			expect(result.text).toContain("3");
			expect(await readFile(path.join(run.cwd, "a.js"), "utf8")).toContain("export const b = 40;");
		} finally {
			await run.dispose();
		}
	}, 30_000);

	test("on: a rejected edit does not run the command and says so", async () => {
		const run = await scenario("  run: true\n");
		try {
			await run.tag();
			const result = await run.call("edit", "e1", { input: run.patch("0000", "export const b = 50;"), run: "touch ran" });
			expect(result.isError).toBe(true);
			expect(result.text).toContain("Run `touch ran` skipped: the edit was not applied.");
			expect(await Bun.file(path.join(run.cwd, "ran")).exists()).toBe(false);
		} finally {
			await run.dispose();
		}
	}, 30_000);

	test("on: an edit that leaves new syntax errors does not run the command", async () => {
		const run = await scenario("  run: true\n  guards:\n    syntaxCheck: true\n");
		try {
			const result = await run.call("edit", "e1", { input: run.patch(await run.tag(), "export const b = (;"), run: "touch ran" });
			expect(result.text).toContain("Run `touch ran` skipped: a.js has new syntax errors");
			expect(await Bun.file(path.join(run.cwd, "ran")).exists()).toBe(false);
		} finally {
			await run.dispose();
		}
	}, 30_000);

	test("on: without a bash tool the command is skipped with a reason", async () => {
		const run = await scenario("  run: true\n", "read,edit");
		try {
			const result = await run.call("edit", "e1", { input: run.patch(await run.tag(), "export const b = 60;"), run: "touch ran" });
			expect(result.text).toContain("Run `touch ran` skipped: the `bash` tool is not available in this session.");
			expect(await readFile(path.join(run.cwd, "a.js"), "utf8")).toContain("export const b = 60;");
			expect(await Bun.file(path.join(run.cwd, "ran")).exists()).toBe(false);
		} finally {
			await run.dispose();
		}
	}, 30_000);

	test("eval: `tool.edit(string)` and `tool.read(string)` take the string as the single input", async () => {
		const run = await scenario("", "read,edit,eval");
		try {
			const fileTag = await run.tag();
			const code = `const edited = await tool.edit(${JSON.stringify(run.patch(fileTag, "export const b = 70;"))});
const read = await tool.read("a.js");
return JSON.stringify({ edited, read });`;
			const result = await run.call("eval", "x1", { language: "js", code });
			expect(result.text).not.toContain("must be an object");
			expect(result.text).toContain("export const b = 70;");
			expect(await readFile(path.join(run.cwd, "a.js"), "utf8")).toContain("export const b = 70;");
		} finally {
			await run.dispose();
		}
	}, 60_000);
});
