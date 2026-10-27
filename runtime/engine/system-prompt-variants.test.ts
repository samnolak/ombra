import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { getAgentDir, setAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { createEngineSession, type EngineSessionResult } from "./session-factory";

// H7 (runtime/harness-bench): `systemPrompt.modelVariants` maps a model-selector substring to a bundled base
// template. It must follow the active model: a switch to a model the pattern does not match restores the default
// prompt, even when the prompt does not show the model id. Unset, every model gets the default prompt.
describe("systemPrompt.modelVariants", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-system-prompt-variants");
	const agentDir = path.join(testRoot, "agent");
	const previousAgentDir = getAgentDir();
	const extension = path.join(testRoot, "provider.ts");
	// The variant's own heading; the default prompt opens with the RFC 2119 legend instead.
	const variantMarker = "# Autonomy and persistence";
	const defaultMarker = "RFC 2119 keywords";

	beforeAll(async () => {
		await mkdir(agentDir, { recursive: true });
		setAgentDir(agentDir);
		const model = (id: string) => `{ id: "${id}", name: "${id}", reasoning: false, input: ["text"],
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }`;
		await writeFile(extension, `export default api => api.registerProvider("variant-probe", {
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
			"--model", `variant-probe/${modelId}`, "--no-session",
		];
		if (config !== undefined) {
			const overlay = path.join(cwd, "overlay.yml");
			await writeFile(overlay, config);
			args.push("--config", overlay);
		}
		return createEngineSession(cwd, args);
	};
	const basePrompt = (created: EngineSessionResult) => created.session.systemPrompt[0];
	const switchTo = async (created: EngineSessionResult, modelId: string) => {
		const target = created.session.modelRegistry.find("variant-probe", modelId);
		if (!target) throw new Error(`model ${modelId} not registered`);
		await created.runInContext(() => created.session.setModel(target));
		expect(created.session.model?.id).toBe(modelId);
	};

	test("unset leaves the default prompt; a match selects the variant and follows model switches", async () => {
		const unset = await open("gpt-probe");
		let defaultPrompt: string;
		try {
			defaultPrompt = basePrompt(unset);
			expect(defaultPrompt).toContain(defaultMarker);
			expect(defaultPrompt).not.toContain(variantMarker);
		} finally {
			await unset.runInContext(() => unset.session.dispose());
		}

		// Upper-case pattern: matching is case-insensitive. The model id stays out of the prompt, so only the
		// variant selection itself can trigger the rebuild on a switch.
		const config = "systemPrompt:\n  modelVariants:\n    GPT-PROBE: system-prompt-gpt\nincludeModelInPrompt: false\n";
		const nonMatching = await open("plain-probe", config);
		try {
			expect(basePrompt(nonMatching)).toBe(defaultPrompt);
		} finally {
			await nonMatching.runInContext(() => nonMatching.session.dispose());
		}

		const matching = await open("gpt-probe", config);
		try {
			const variantPrompt = basePrompt(matching);
			expect(variantPrompt).toContain(variantMarker);
			expect(variantPrompt).not.toContain(defaultMarker);
			// The project block (context files, workstation, workspace) is shared by both templates.
			expect(matching.session.systemPrompt.slice(1).join("\n")).toContain("<project-context>");

			await switchTo(matching, "plain-probe");
			expect(basePrompt(matching)).toBe(defaultPrompt);

			await switchTo(matching, "gpt-probe");
			expect(basePrompt(matching)).toBe(variantPrompt);
		} finally {
			await matching.runInContext(() => matching.session.dispose());
		}
	}, 60_000);

	// The GPT prompt reads named paths without a listing first, and with `edit.run` on asks for an edit's check in
	// its `run` instead of a bash call from an eval cell.
	test("system-prompt-gpt: direct reads of known paths; the edit check follows edit.run", async () => {
		const config = (editRun: boolean) =>
			`systemPrompt:\n  modelVariants:\n    gpt-: system-prompt-gpt\nedit:\n  run: ${editRun}\n`;
		const off = await open("gpt-probe", config(false));
		const on = await open("gpt-probe", config(true));
		try {
			const offPrompt = basePrompt(off);
			const onPrompt = basePrompt(on);
			for (const prompt of [offPrompt, onPrompt]) {
				expect(prompt).toContain(variantMarker);
				expect(prompt).toContain("Paths named in the request or seen in tool output are known: read them directly");
				expect(prompt).not.toContain("never a guessed path");
			}
			expect(offPrompt).toContain("await tool.bash({ command: … })");
			expect(offPrompt).not.toContain("edit's `run`");
			expect(onPrompt).toContain("put the check (the test or build command) in that edit's `run`");
			expect(onPrompt).not.toContain("await tool.bash({ command: … })");
		} finally {
			await off.runInContext(() => off.session.dispose());
			await on.runInContext(() => on.session.dispose());
		}
	}, 60_000);
});
