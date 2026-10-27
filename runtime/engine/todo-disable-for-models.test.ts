import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { getAgentDir, setAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { createEngineSession, type EngineSessionResult } from "./session-factory";

// `todo.disableForModels` removes the todo tool for models whose selector contains an entry (case-insensitive)
// and follows model switches; other models keep it, as does every model while the list is empty.
describe("todo.disableForModels", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-todo-models");
	const agentDir = path.join(testRoot, "agent");
	const previousAgentDir = getAgentDir();
	const extension = path.join(testRoot, "provider.ts");

	beforeAll(async () => {
		await mkdir(agentDir, { recursive: true });
		setAgentDir(agentDir);
		const model = (id: string) => `{ id: "${id}", name: "${id}", reasoning: false, input: ["text"],
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }`;
		await writeFile(extension, `export default api => api.registerProvider("todo-probe", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-test-key", api: "openai-completions",
			models: [${model("gpt-probe")}, ${model("plain-probe")}],
		});`);
	});
	afterAll(async () => {
		setAgentDir(previousAgentDir);
		await rm(testRoot, { recursive: true, force: true });
	});

	const open = async (modelId: string, config?: string): Promise<EngineSessionResult> => {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		const args = [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--model", `todo-probe/${modelId}`, "--no-session",
		];
		if (config !== undefined) {
			const overlay = path.join(cwd, "overlay.yml");
			await writeFile(overlay, config);
			args.push("--config", overlay);
		}
		return createEngineSession(cwd, args);
	};
	const hasTodo = (created: EngineSessionResult) => created.session.getActiveToolNames().includes("todo");
	const switchTo = async (created: EngineSessionResult, modelId: string) => {
		const target = created.session.modelRegistry.find("todo-probe", modelId);
		if (!target) throw new Error(`model ${modelId} not registered`);
		await created.runInContext(() => created.session.setModel(target));
		expect(created.session.model?.id).toBe(modelId);
	};

	test("unset keeps todo for every model", async () => {
		const created = await open("gpt-probe");
		try {
			expect(hasTodo(created)).toBe(true);
		} finally {
			await created.runInContext(() => created.session.dispose());
		}
	}, 60_000);

	test("a matching model has no todo; the tool follows model switches", async () => {
		const config = "todo:\n  disableForModels:\n    - GPT-\n";
		const plain = await open("plain-probe", config);
		try {
			expect(hasTodo(plain)).toBe(true);
		} finally {
			await plain.runInContext(() => plain.session.dispose());
		}

		const created = await open("gpt-probe", config);
		try {
			expect(hasTodo(created)).toBe(false);
			expect(created.session.getToolByName("todo")).toBeUndefined();

			await switchTo(created, "plain-probe");
			expect(hasTodo(created)).toBe(true);

			await switchTo(created, "gpt-probe");
			expect(hasTodo(created)).toBe(false);
		} finally {
			await created.runInContext(() => created.session.dispose());
		}
	}, 60_000);

	test("todo.enabled: false still turns it off for every model", async () => {
		const created = await open("plain-probe", "todo:\n  enabled: false\n  disableForModels:\n    - gpt-\n");
		try {
			expect(hasTodo(created)).toBe(false);
		} finally {
			await created.runInContext(() => created.session.dispose());
		}
	}, 60_000);
});
