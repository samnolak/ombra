import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import type { EditMode } from "@oh-my-pi/pi-tui/tools/edit";
import { Settings } from "../config/settings";
import type { ToolSession } from "../tools";
import { findNewSyntaxIssues, pythonIndentationIssues } from "./guards";
import { EditTool } from "./index";
import { getEditStore } from "./store";

// Lab FA #2: `PUT N.=N` used where an insert was meant silently deleted a needed line (astropy-13453,
// scikit-learn-25232, matplotlib-14623/20676). The guards are warn-only and default off.
const ALL_ON = {
	"edit.guards.reportDeletions": true,
	"edit.guards.rejectionContext": true,
	"edit.guards.syntaxCheck": true,
};

const PY = [
	"class Writer:",
	"    def write(self, cols):",
	"        self.data.header.cols = cols",
	"        self.data._set_fill_values(cols)",
	"        lines = []",
	"        if self._interactive:",
	"            lines.append(1)",
	"            lines.append(2)",
	"        return lines",
	"",
].join("\n");

const NUMBERED = `${Array.from({ length: 30 }, (_, i) => `x${i + 1} = ${i + 1}`).join("\n")}\n`;

const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "edit-guards");
beforeAll(() => mkdir(testRoot, { recursive: true }));
afterAll(() => rm(testRoot, { recursive: true, force: true }));

async function setup(overrides: Record<string, unknown>, files: Record<string, string>) {
	const cwd = await mkdtemp(path.join(testRoot, "run-"));
	for (const [name, text] of Object.entries(files)) await writeFile(path.join(cwd, name), text);
	const session = {
		cwd,
		hasUI: false,
		enableLsp: false,
		settings: Settings.isolated(overrides),
		getSessionFile: () => null,
	} as unknown as ToolSession;
	const store = getEditStore(session);
	/** Record a read of `name` that displayed `seen` (every line when omitted); returns its tag. */
	const read = (name: string, seen?: number[]) => {
		const file = path.join(cwd, name);
		const text = files[name];
		return store.recordSnapshot(file, text, seen ?? text.split("\n").map((_, i) => i + 1));
	};
	const edit = async (mode: EditMode, params: Record<string, unknown>) => {
		const result = await new EditTool(session, mode).execute(`call-${Math.random()}`, params as never);
		return { isError: result.isError === true, texts: result.content.map(c => ("text" in c ? c.text : "")) };
	};
	return { cwd, store, read, edit, disk: (name: string) => readFile(path.join(cwd, name), "utf8") };
}

describe("edit.guards.reportDeletions", () => {
	test("PUT N.=N that overwrote a kept line lists it with the insert hint", async () => {
		const t = await setup(ALL_ON, { "w.py": PY });
		const tag = t.read("w.py");
		const result = await t.edit("hashline", { input: `[w.py#${tag}]\nPUT 4.=4:\n+        self.data._set_col_formats()\n` });
		expect(result.isError).toBe(false);
		const note = result.texts.slice(1).join("\n");
		expect(note).toContain("removed 1 line(s) of w.py that appear nowhere in the replacement");
		expect(note).toContain("  4:        self.data._set_fill_values(cols)");
		expect(note).toContain("If you meant to insert, use PUT >N / PUT <N; restore lines you meant to keep.");
	});

	test("off by default: the same edit carries no warning", async () => {
		const t = await setup({}, { "w.py": PY });
		const tag = t.read("w.py");
		const result = await t.edit("hashline", { input: `[w.py#${tag}]\nPUT 4.=4:\n+        self.data._set_col_formats()\n` });
		expect(result.isError).toBe(false);
		expect(result.texts).toHaveLength(1);
	});

	test("inserts, kept lines and explicit CUTs are not reported", async () => {
		const t = await setup(ALL_ON, { "w.py": PY });
		let tag = t.read("w.py");
		// Insert after 4; replace 5 with a body that keeps it.
		let result = await t.edit("hashline", {
			input: `[w.py#${tag}]\nPUT >4:\n+        self.data._set_col_formats()\nPUT 5.=5:\n+        lines = []\n+        extra = 0\n`,
		});
		expect(result.isError).toBe(false);
		expect(result.texts).toHaveLength(1);
		// Range and block CUTs are deliberate deletions.
		const current = await t.disk("w.py");
		tag = t.store.recordSnapshot(path.join(t.cwd, "w.py"), current, current.split("\n").map((_, i) => i + 1));
		result = await t.edit("hashline", { input: `[w.py#${tag}]\nCUT 3.=3\nCUT 8*\n` });
		expect(result.isError).toBe(false);
		expect(await t.disk("w.py")).not.toContain("if self._interactive:");
		expect(result.texts.join("\n")).not.toContain("appear nowhere in the replacement");
	});

	test("replace mode reports a dropped anchor line; apply_patch deletions are explicit", async () => {
		const t = await setup(ALL_ON, { "w.py": PY, "v.py": PY });
		const replaced = await t.edit("replace", {
			path: "w.py",
			old_string: "        lines = []\n",
			new_string: "        self.data._set_col_formats()\n",
		});
		expect(replaced.texts.join("\n")).toContain("  5:        lines = []");
		expect(replaced.texts.join("\n")).toContain("keep the anchor text in new_string");
		const patched = await t.edit("apply_patch", {
			input: "*** Begin Patch\n*** Update File: v.py\n@@\n-        lines = []\n+        rows = []\n*** End Patch",
		});
		expect(patched.isError).toBe(false);
		expect(patched.texts.join("\n")).not.toContain("appear nowhere in the replacement");
	});
});

describe("edit.guards.rejectionContext", () => {
	test("never-displayed anchors return ±3 current rows under a tag that a straight retry can use", async () => {
		const t = await setup(ALL_ON, { "n.py": NUMBERED });
		const tag = t.read("n.py", [1, 2, 3]);
		const rejected = await t.edit("hashline", { input: `[n.py#${tag}]\nPUT 20.=20:\n+y = 1\n` });
		expect(rejected.isError).toBe(true);
		const context = rejected.texts[1];
		expect(context).toContain(`[n.py#${tag}]`);
		expect(context).toContain("17:x17 = 17\n18:x18 = 18\n19:x19 = 19\n20:x20 = 20\n21:x21 = 21\n22:x22 = 22\n23:x23 = 23");
		expect(context).not.toContain("16:x16");
		// The model meant to insert after 18 — a line only the context displayed.
		const retried = await t.edit("hashline", { input: `[n.py#${tag}]\nPUT >18:\n+y = 1\n` });
		expect(retried.isError).toBe(false);
		expect(await t.disk("n.py")).toContain("x18 = 18\ny = 1\nx19 = 19");
	});

	test("a stale tag gets the current file's tag and rows", async () => {
		const t = await setup(ALL_ON, { "n.py": NUMBERED });
		const tag = t.read("n.py");
		await writeFile(path.join(t.cwd, "n.py"), NUMBERED.replace("x10 = 10\nx11 = 11", "a = 0\nx10 = 99\nx11 = 98"));
		const rejected = await t.edit("hashline", { input: `[n.py#${tag}]\nPUT 10.=11:\n+y = 1\n` });
		expect(rejected.isError).toBe(true);
		const fresh = /\[n\.py#([0-9A-F]{4})\]/.exec(rejected.texts[1] ?? "")?.[1];
		expect(fresh).toBeDefined();
		expect(fresh).not.toBe(tag);
		expect(rejected.texts[1]).toContain("7:x7 = 7");
		expect(rejected.texts[1]).toContain("10:a = 0");
		expect(rejected.texts[1]).toContain("14:x13 = 13");
		const retried = await t.edit("hashline", { input: `[n.py#${fresh}]\nPUT 10.=10:\n+b = 0\n` });
		expect(retried.isError).toBe(false);
	});

	test("off: the rejection is the native message alone", async () => {
		const t = await setup({}, { "n.py": NUMBERED });
		const tag = t.read("n.py", [1, 2, 3]);
		const rejected = await t.edit("hashline", { input: `[n.py#${tag}]\nPUT 20.=20:\n+y = 1\n` });
		expect(rejected.isError).toBe(true);
		expect(rejected.texts).toHaveLength(1);
	});
});

describe("edit.guards.syntaxCheck", () => {
	test("a tree-sitter regression is reported with its line and current rows", async () => {
		const t = await setup(ALL_ON, { "w.py": PY });
		const result = await t.edit("replace", { path: "w.py", old_string: "return lines", new_string: "return (lines" });
		const note = result.texts.slice(1).join("\n");
		expect(note).toContain("this edit introduced syntax errors in w.py");
		expect(note).toContain("line 9: parse error introduced by this change");
		expect(note).toContain("9:        return (lines");
		expect(note).not.toContain("no longer parses after this edit");
	});

	test("off: the generic parse warning stays", async () => {
		const t = await setup({}, { "w.py": PY });
		const result = await t.edit("replace", { path: "w.py", old_string: "return lines", new_string: "return (lines" });
		expect(result.texts.slice(1).join("\n")).toContain("w.py no longer parses after this edit");
	});

	test("a replaced block header leaves an unexpected indent that tree-sitter accepts (matplotlib-20676)", async () => {
		const on = await setup(ALL_ON, { "w.py": PY });
		const input = (tag: string) => `[w.py#${tag}]\nPUT 6.=6:\n+        handles = []\n`;
		const result = await on.edit("hashline", { input: input(on.read("w.py")) });
		expect(result.isError).toBe(false);
		const note = result.texts.slice(1).join("\n");
		expect(note).toContain("line 7: unexpected indent");
		// The dropped header is reported too, and the excerpt carries the post-edit tag.
		expect(note).toContain("6:        if self._interactive:");
		expect(note).toMatch(/\[w\.py#[0-9A-F]{4}\]\n6: {8}handles = \[\]\n7: {12}lines\.append\(1\)/);

		const off = await setup({}, { "w.py": PY });
		const plain = await off.edit("hashline", { input: input(off.read("w.py")) });
		expect(plain.texts).toHaveLength(1);
	});

	test("apply_patch shares the syntax check", async () => {
		const t = await setup(ALL_ON, { "w.py": PY });
		const result = await t.edit("apply_patch", {
			input: "*** Begin Patch\n*** Update File: w.py\n@@\n-        if self._interactive:\n+        if self._interactive\n*** End Patch",
		});
		expect(result.texts.slice(1).join("\n")).toContain("line 6: parse error introduced by this change");
	});

	test("pre-existing indentation problems are not reported again", () => {
		const prev = "def f():\n    x = 1\n        y = 2\n    return x\n";
		const next = "def f():\n    x = 3\n        y = 2\n    return x\n";
		expect(pythonIndentationIssues(prev)).toEqual([{ line: 3, message: "unexpected indent" }]);
		expect(findNewSyntaxIssues({ path: "/r/a.py", displayPath: "a.py", prev, next }, false)).toEqual([]);
	});

	test("the Python indentation scan accepts strings, brackets, continuations and comments", () => {
		const valid = [
			"import os",
			"x = {",
			"        'a': 1,  # comment with 'quote'",
			"  }",
			'doc = """',
			"    indented text that is not code:",
			'"""',
			"total = 1 + \\",
			"        2",
			"if x:  # trailing comment",
			"",
			"    # comment at another depth",
			"\tpass",
			"else:",
			"    s = 'a\\'b:'",
			"    f = lambda v: (",
			"        v)",
			"def g(a,",
			"      b):",
			"    return a",
		].join("\n");
		expect(pythonIndentationIssues(valid)).toEqual([]);
		expect(pythonIndentationIssues("if x:\ny = 1\n")).toEqual([{ line: 2, message: "expected an indented block" }]);
		expect(pythonIndentationIssues("if x:\n    y = 1\n  z = 2\n")).toEqual([
			{ line: 3, message: "unindent does not match any outer indentation level" },
		]);
	});

	test("stays cheap on a large file", () => {
		const body = Array.from({ length: 1500 }, (_, i) => `def f${i}(a):\n    return (a, ${i})\n`).join("");
		const next = body.replace("return (a, 700)", "return (a, 700");
		const started = performance.now();
		const issues = findNewSyntaxIssues({ path: "/r/big.py", displayPath: "big.py", prev: body, next }, true);
		const elapsed = performance.now() - started;
		expect(issues).toEqual([{ start: 1402, end: 1402, message: "parse error introduced by this change" }]);
		expect(elapsed).toBeLessThan(250);
	});
});
