import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdtemp, rm } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { buildSystemPrompt } from "./system-prompt";

const PRECEDENCE =
	"They run broad to specific: nearer files override broader ones; none override system or direct user instructions.";

describe("contextFiles.maxBytes budget in the system prompt", () => {
	let cwd: string;
	beforeAll(async () => {
		cwd = await mkdtemp(path.join(os.tmpdir(), "ombra-context-budget-"));
	});
	afterAll(() => rm(cwd, { recursive: true, force: true }));

	const userFile = { path: "/home/u/.omp/agent/AGENTS.md", content: "user rule: answer in English" };
	const lines = (label: string) =>
		[`${label} rule`, ...Array.from({ length: 300 }, (_, i) => `${label} line ${String(i).padStart(3, "0")}`)].join("\n");
	const rootFile = { path: "/repo/AGENTS.md", content: lines("top"), depth: 2 };
	const midFile = { path: "/repo/pkg/AGENTS.md", content: lines("mid"), depth: 1 };
	const nearFile = { path: "/repo/pkg/app/AGENTS.md", content: "near rule: use tabs", depth: 0 };
	const files = [userFile, rootFile, midFile, nearFile];

	async function projectBlock(
		contextFilesMaxBytes?: number,
		contextFiles: Array<{ path: string; content: string; depth?: number }> = files,
	): Promise<string> {
		const { systemPrompt } = await buildSystemPrompt({
			cwd,
			contextFiles,
			contextFilesMaxBytes,
			skills: [],
			personality: "none",
			activeRepoContext: null,
		});
		const block = systemPrompt.find(part => part.includes("<repo-rules>"));
		expect(block).toBeDefined();
		return block!;
	}

	test("budget 0 is unlimited and adds only the static precedence sentence", async () => {
		const block = await projectBlock(0);
		expect(block).toBe(await projectBlock(undefined));
		expect(block).toContain(PRECEDENCE);
		for (const file of files) expect(block).toContain(`<file path="${file.path}">\n${file.content}\n</file>`);
		expect(block).not.toContain("Workspace instructions exceeded");
	});

	test("under budget, every file renders whole with no notice", async () => {
		const block = await projectBlock(1 << 20);
		expect(block).toBe(await projectBlock(0));
	});

	test("over budget, the nearest file stays whole and the notice names cut paths", async () => {
		const block = await projectBlock(2000);
		// User-global chain has its own budget: untouched by the large project chain.
		expect(block).toContain(`<file path="${userFile.path}">\n${userFile.content}\n</file>`);
		expect(block).toContain(`<file path="${nearFile.path}">\n${nearFile.content}\n</file>`);
		// Next-nearest file fills the remaining 1981 bytes, cut on a line boundary; the broadest is gone.
		const midKept = midFile.content.slice(0, midFile.content.indexOf("\nmid line 151"));
		expect(midKept.endsWith("mid line 150")).toBe(true);
		expect(block).toContain(`<file path="${midFile.path}">\n${midKept}\n</file>`);
		expect(block).not.toContain(`${rootFile.path}">`);
		expect(block).toContain(
			`Workspace instructions exceeded the 2000-byte budget: truncated ${midFile.path} (1971 of 3908 bytes); omitted ${rootFile.path}. Read these files in full before work they may govern.`,
		);
		// Byte-stable across rebuilds (no per-turn content).
		expect(block).toBe(await projectBlock(2000));
	});

	test("a budget that omits every file still renders the notice", async () => {
		const multibyte = { path: "/repo/AGENTS.md", content: "Ж rule", depth: 0 };
		const block = await projectBlock(1, [multibyte]);
		expect(block).toContain(
			`<repo-rules>\nWorkspace instructions exceeded the 1-byte budget: omitted ${multibyte.path}. Read these files in full before work they may govern.\n</repo-rules>`,
		);
		expect(block).not.toContain("<file path=");
		expect(block).not.toContain(PRECEDENCE);
	});
});
