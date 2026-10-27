import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { createEngineSession } from "./session-factory";

// Lab (why-codex-wins §2.1): 53 of 54 rejected hashline edits in the polyglot screen were "anchors … never
// displayed" on a tag minted by our own edit result, and none of them anchored on a row that result showed: they
// anchored on the middle of a run the model had just written, which the result elides as `…`.
// `edit.seenFromEditResults` counts the lines an edit wrote as displayed for the new tag.
describe("edit.seenFromEditResults", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-edit-seen-from-results");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	const text = (result: { content: { type: string; text?: string }[] }) =>
		result.content.map(c => c.text ?? "").join("\n");
	const putLine = (tag: string, line: number, content: string) =>
		`*** Begin Patch\n[a.js#${tag}]\nPUT ${line}.=${line}:\n+${content}\n*** End Patch\n`;

	async function scenario(seenFromEditResults: boolean) {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		const original = Array.from({ length: 12 }, (_, i) => `export const k${i + 1} = ${i + 1};`).join("\n");
		await writeFile(path.join(cwd, "a.js"), `${original}\n`);
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("seen-edit", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }]
		});`);
		const config = path.join(cwd, "config.yml");
		await writeFile(config, `edit:\n  mode: hashline\n  seenFromEditResults: ${seenFromEditResults}\n`);
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--tools", "read,edit", "--model", "seen-edit/model", "--no-session", "--config", config,
		]);
		const read = created.session.getToolByName("read");
		const edit = created.session.getToolByName("edit");
		if (!read || !edit) throw new Error("read/edit tool missing");
		const tagOf = (output: string) => {
			const match = /\[a\.js#([0-9A-F]{4})\]/.exec(output);
			if (!match) throw new Error(`no tag in:\n${output}`);
			return match[1];
		};
		const tag1 = tagOf(text(await created.runInContext(() => read.execute("r1", { path: "a.js" }))));
		// Insert a 40-line function after line 1; the edit result shows its edges and elides the middle.
		const body = Array.from({ length: 40 }, (_, i) => `+  const v${i} = ${i};`).join("\n");
		const input = `*** Begin Patch\n[a.js#${tag1}]\nPUT >1:\n+export function f() {\n${body}\n+}\n*** End Patch\n`;
		const first = text(await created.runInContext(() => edit.execute("e1", { input })));
		const editLine = (id: string, line: number, content: string) =>
			created.runInContext(() => edit.execute(id, { input: putLine(tagOf(first), line, content) })).then(
				result => text(result),
				(error: unknown) => String(error),
			);
		return { created, first, editLine, file: path.join(cwd, "a.js") };
	}

	test("off (default): a line the edit wrote but its result elided is rejected", async () => {
		const run = await scenario(false);
		try {
			// Line 22 (`const v19`) sits in the elided middle of the inserted run.
			expect(run.first).toContain("\n3:  const v0 = 0;\n…\n42:  const v39 = 39;\n");
			expect(await run.editLine("e2", 22, "  const v19 = 1900;")).toContain("never displayed");
		} finally {
			await run.created.session.dispose();
		}
	}, 30_000);

	test("off (default): a row the edit result showed is already anchorable", async () => {
		const run = await scenario(false);
		try {
			expect(await run.editLine("e2", 42, "  const v39 = 3900;")).not.toContain("never displayed");
			expect(await readFile(run.file, "utf8")).toContain("  const v39 = 3900;\n}");
		} finally {
			await run.created.session.dispose();
		}
	}, 30_000);

	test("on: the elided written line is accepted without a re-read", async () => {
		const run = await scenario(true);
		try {
			expect(run.first).not.toMatch(/^22:/m);
			expect(await run.editLine("e2", 22, "  const v19 = 1900;")).not.toContain("never displayed");
			expect(await readFile(run.file, "utf8")).toContain("  const v18 = 18;\n  const v19 = 1900;\n  const v20 = 20;");
		} finally {
			await run.created.session.dispose();
		}
	}, 30_000);

	test("on: a line the edit did not write and its result did not show is still rejected", async () => {
		const run = await scenario(true);
		try {
			// Line 52 (`export const k10`) was untouched; only the read showed it, under the old tag.
			expect(run.first).not.toMatch(/^52:/m);
			expect(await run.editLine("e3", 52, "export const k10 = 100;")).toContain("never displayed");
		} finally {
			await run.created.session.dispose();
		}
	}, 30_000);
});
