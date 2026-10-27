import { afterAll, afterEach, beforeAll, describe, expect, spyOn, test } from "bun:test";
import { existsSync, readFileSync, realpathSync } from "node:fs";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { getAgentDir, setAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { runConfigCommand } from "./coding-agent/src/cli/config-cli";
import { resetSettingsForTest, Settings } from "./coding-agent/src/config/settings";
import { HARNESS_DIR_ENV } from "./coding-agent/src/harness-layer";
import { discoverAgents } from "./coding-agent/src/task/discovery";
import { cfgTaskMaxConcurrency } from "./coding-agent/src/task/settings";
import { cfgAsyncMaxJobs } from "./coding-agent/src/tools/settings";
import { createEngineSession, type EngineSessionResult } from "./session-factory";

// Ombra ships an app-owned harness (rules, agents, extensions, skills, settings defaults) and names it through
// OMBRA_HARNESS_DIR for every engine, TUI and CLI launch. It loads below the user's own ~/.omp/agent and the
// project and above omp's bundled items and registry defaults, is never written to, and is absent when unset.
describe("Ombra harness layer", () => {
	// Real path: discovery reports resolved paths, and macOS tmp is a symlink.
	const testRoot = path.join(realpathSync(os.tmpdir()), "ombra-tests", `run-${process.pid}`, "engine-harness-layer");
	const home = path.join(testRoot, "home");
	const agentDir = path.join(home, ".omp", "agent");
	const harness = path.join(testRoot, "harness");
	const markers = path.join(testRoot, "markers");
	const marker = (name: string) => path.join(markers, `${name}.ran`);
	const previousAgentDir = getAgentDir();
	const previousAgentDirEnv = process.env.PI_CODING_AGENT_DIR;
	const previousHarness = process.env[HARNESS_DIR_ENV];
	const harnessConfig = "task:\n  maxConcurrency: 3\nasync:\n  maxJobs: 7\n";

	const writeIn = async (root: string, file: string, content: string) => {
		await mkdir(path.dirname(path.join(root, file)), { recursive: true });
		await writeFile(path.join(root, file), content);
	};
	const skill = (name: string, body: string) => `---\nname: ${name}\ndescription: Probe skill ${name}\n---\n${body}\n`;
	const agent = (name: string, description: string) =>
		`---\nname: ${name}\ndescription: ${description}\ntools: [bash]\n---\nDo the probe work.\n`;
	// A conditional (TTSR) rule scoped to .tsx files: registered with the session's rule manager when discovered.
	const rule = (description: string, body: string) =>
		`---\ndescription: ${description}\ncondition: ["*.tsx"]\n---\n${body}\n`;
	// The extension factory runs once per session load, so a fresh marker proves this session loaded it.
	const extension = (name: string) =>
		`import { writeFileSync } from "node:fs";\nexport default () => { writeFileSync(${JSON.stringify(marker(name))}, "1"); };\n`;

	beforeAll(async () => {
		await mkdir(markers, { recursive: true });
		// The harness, laid out like harness/profile
		await writeIn(harness, "skills/harness-probe-skill/SKILL.md", skill("harness-probe-skill", "Harness skill body."));
		await writeIn(harness, "skills/shared-skill/SKILL.md", skill("shared-skill", "Harness copy of the shared skill."));
		await writeIn(harness, "agent/agents/harness-probe-agent.md", agent("harness-probe-agent", "Harness probe agent"));
		await writeIn(harness, "agent/agents/reviewer.md", agent("reviewer", "Harness reviewer"));
		await writeIn(harness, "agent/agents/shared-agent.md", agent("shared-agent", "Harness shared agent"));
		await writeIn(harness, "agent/extensions/harness-probe-ext/index.ts", extension("harness-probe-ext"));
		await writeIn(harness, "agent/extensions/shared-ext/index.ts", extension("harness-shared-ext"));
		await writeIn(harness, "agent/AGENTS.md", "Harness rule: answer in haiku.\n");
		await writeIn(harness, "agent/rules/harness-tsx.md", rule("Harness TSX rule", "Prefer function components."));
		await writeIn(harness, "agent/rules/shared-rule.md", rule("Harness shared rule", "Harness copy of the shared rule."));
		await writeIn(harness, "agent/config.yml", harnessConfig);
		// The user's own agent dir: same-name items and one setting of their own
		await writeIn(agentDir, "skills/shared-skill/SKILL.md", skill("shared-skill", "User copy of the shared skill."));
		await writeIn(agentDir, "agents/shared-agent.md", agent("shared-agent", "User shared agent"));
		await writeIn(agentDir, "extensions/shared-ext/index.ts", extension("user-shared-ext"));
		await writeIn(agentDir, "AGENTS.md", "User rule: answer in prose.\n");
		await writeIn(agentDir, "rules/shared-rule.md", rule("User shared rule", "User copy of the shared rule."));
		await writeIn(agentDir, "config.yml", "async:\n  maxJobs: 9\n");
		setAgentDir(agentDir);
	});
	afterEach(() => {
		if (previousHarness === undefined) delete process.env[HARNESS_DIR_ENV];
		else process.env[HARNESS_DIR_ENV] = previousHarness;
	});
	afterAll(async () => {
		resetSettingsForTest();
		setAgentDir(previousAgentDir);
		if (previousAgentDirEnv === undefined) delete process.env.PI_CODING_AGENT_DIR;
		await rm(testRoot, { recursive: true, force: true });
	});

	// User task agents are read from os.homedir(), which Bun fixes at process start: the precedence of user,
	// harness and bundled agents is checked in a child process whose HOME is the isolated one.
	const agentsInIsolatedHome = async (withHarness: boolean): Promise<Record<string, string>> => {
		const script = path.join(testRoot, "agents-probe.ts");
		await writeFile(script, `import { discoverAgents } from ${JSON.stringify(path.join(import.meta.dir, "coding-agent/src/task/discovery"))};\n`
			+ `const { agents } = await discoverAgents(process.argv[2]);\n`
			+ `console.log(JSON.stringify(Object.fromEntries(agents.map(agent => [agent.name, agent.description]))));\n`);
		const env: Record<string, string | undefined> = { ...process.env, HOME: home, PI_CODING_AGENT_DIR: agentDir };
		if (withHarness) env[HARNESS_DIR_ENV] = harness;
		else delete env[HARNESS_DIR_ENV];
		const child = Bun.spawn([process.execPath, "--no-install", script, await mkdtemp(path.join(testRoot, "agents-"))], {
			env, stdout: "pipe", stderr: "pipe",
		});
		const [stdout, stderr, code] = await Promise.all([new Response(child.stdout).text(), new Response(child.stderr).text(), child.exited]);
		if (code !== 0) throw new Error(`agent discovery probe failed (${code}): ${stderr}`);
		return JSON.parse(stdout.trim().split("\n").at(-1) ?? "{}");
	};

	// The model comes from an explicit extension outside every config dir (explicit paths always load)
	const providerFlags = async (name: string) => {
		const provider = path.join(testRoot, `provider-${name}.ts`);
		await writeFile(provider, `export default api => api.registerProvider("harness-probe", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-probe-key", api: "openai-completions",
			models: [{ id: "m", name: "m", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }],
		});`);
		return ["--extension", provider, "--model", "harness-probe/m", "--no-prewalk", "--no-session", "--no-lsp"];
	};
	const view = async (created: EngineSessionResult, cwd: string) => {
		const { agents } = await created.runInContext(() => discoverAgents(cwd));
		const prompt = created.session.systemPrompt.join("\n");
		return {
			skills: created.session.skills.map(item => ({ name: item.name, filePath: item.filePath })),
			agents: Object.fromEntries(agents.map(item => [item.name, item.description])),
			prompt,
			ran: (name: string) => existsSync(marker(name)),
			maxConcurrency: cfgTaskMaxConcurrency.get(created.session.settings),
			maxJobs: cfgAsyncMaxJobs.get(created.session.settings),
			rules: Object.fromEntries((created.session.ttsrManager?.getRules() ?? []).map(item => [item.name, item.path])),
		};
	};
	const openSession = async (label: string, extra: string[] = []) => {
		const cwd = await mkdtemp(path.join(testRoot, `${label}-`));
		await rm(markers, { recursive: true, force: true });
		await mkdir(markers, { recursive: true });
		const created = await createEngineSession(cwd, [...(await providerFlags(label)), ...extra]);
		try {
			return await view(created, cwd);
		} finally {
			await created.runInContext(() => created.session.dispose());
		}
	};

	test("(a)(b) harness items load with the variable set, same-name user items win, and nothing loads unset", async () => {
		process.env[HARNESS_DIR_ENV] = harness;
		const set = await openSession("set");
		// Skill: the harness one appears; the user's same-name skill shadows the harness copy without an alias
		expect(set.skills.find(item => item.name === "harness-probe-skill")?.filePath)
			.toBe(path.join(harness, "skills/harness-probe-skill/SKILL.md"));
		expect(set.skills.filter(item => item.name.endsWith("shared-skill"))).toEqual([
			{ name: "shared-skill", filePath: path.join(agentDir, "skills/shared-skill/SKILL.md") },
		]);
		// Agents: the harness agent appears in the session
		expect(set.agents["harness-probe-agent"]).toBe("Harness probe agent");
		// ...the harness reviewer replaces the bundled one, and the user's same-name agent wins over the harness one
		const harnessAgents = await agentsInIsolatedHome(true);
		expect(harnessAgents["harness-probe-agent"]).toBe("Harness probe agent");
		expect(harnessAgents.reviewer).toBe("Harness reviewer");
		expect(harnessAgents["shared-agent"]).toBe("User shared agent");
		const stockAgents = await agentsInIsolatedHome(false);
		expect(stockAgents["harness-probe-agent"]).toBeUndefined();
		expect(stockAgents.reviewer).toBeDefined();
		expect(stockAgents.reviewer).not.toBe("Harness reviewer");
		expect(stockAgents["shared-agent"]).toBe("User shared agent");
		// Extensions: the harness one loads; a same-name user extension loads instead of the harness one
		expect(set.ran("harness-probe-ext")).toBe(true);
		expect(set.ran("user-shared-ext")).toBe(true);
		expect(set.ran("harness-shared-ext")).toBe(false);
		// Rules: the harness AGENTS.md renders, before the user's own, so the user's rules come later
		const harnessRule = set.prompt.indexOf("Harness rule: answer in haiku.");
		const userRule = set.prompt.indexOf("User rule: answer in prose.");
		expect(harnessRule).toBeGreaterThanOrEqual(0);
		expect(userRule).toBeGreaterThan(harnessRule);
		// Settings: the harness default applies despite the engine's protocol-default pin; the user's value wins
		expect(set.maxConcurrency).toBe(3);
		expect(set.maxJobs).toBe(9);
		// Rules: the harness conditional rule is available; the user's same-name rule wins
		expect(set.rules["harness-tsx"]).toBe(path.join(harness, "agent/rules/harness-tsx.md"));
		expect(set.rules["shared-rule"]).toBe(path.join(agentDir, "rules/shared-rule.md"));

		// The harness is app-owned: an untrusted folder drops only its own items
		const untrusted = await openSession("untrusted", ["--untrusted-project"]);
		expect(untrusted.skills.some(item => item.name === "harness-probe-skill")).toBe(true);
		expect(untrusted.agents["harness-probe-agent"]).toBe("Harness probe agent");
		expect(untrusted.ran("harness-probe-ext")).toBe(true);
		expect(untrusted.prompt).toContain("Harness rule: answer in haiku.");
		expect(untrusted.maxConcurrency).toBe(3);
		expect(untrusted.rules["harness-tsx"]).toBe(path.join(harness, "agent/rules/harness-tsx.md"));

		// Unset: stock behaviour
		delete process.env[HARNESS_DIR_ENV];
		const unset = await openSession("unset");
		expect(unset.skills.some(item => item.name === "harness-probe-skill")).toBe(false);
		expect(unset.skills.filter(item => item.name.endsWith("shared-skill"))).toEqual([
			{ name: "shared-skill", filePath: path.join(agentDir, "skills/shared-skill/SKILL.md") },
		]);
		expect(unset.agents["harness-probe-agent"]).toBeUndefined();
		expect(unset.ran("harness-probe-ext")).toBe(false);
		expect(unset.ran("harness-shared-ext")).toBe(false);
		expect(unset.ran("user-shared-ext")).toBe(true);
		expect(unset.prompt).not.toContain("Harness rule");
		expect(unset.prompt).toContain("User rule: answer in prose.");
		expect(unset.maxConcurrency).toBe(cfgTaskMaxConcurrency.default);
		expect(unset.maxJobs).toBe(9);
		expect(unset.rules["harness-tsx"]).toBeUndefined();
		expect(unset.rules["shared-rule"]).toBe(path.join(agentDir, "rules/shared-rule.md"));
	}, 120_000);

	test("(c) settings: project and user config.yml beat the harness, the harness beats the registry default", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "layers-"));
		process.env[HARNESS_DIR_ENV] = harness;
		const settings = await Settings.loadIsolated({ cwd, agentDir });
		expect(cfgTaskMaxConcurrency.get(settings)).toBe(3);
		expect(cfgTaskMaxConcurrency.provenance(settings)).toBe("harness");
		expect(cfgAsyncMaxJobs.get(settings)).toBe(9);
		expect(cfgAsyncMaxJobs.provenance(settings)).toBe("global");
		// Overlay children (subagents) and clones read the same layer
		expect(cfgTaskMaxConcurrency.get(settings.overlay())).toBe(3);
		expect(cfgTaskMaxConcurrency.get(await settings.cloneForCwd(cwd))).toBe(3);
		// A project value and a --config overlay both rank above the harness (in-memory: no user config.yml read)
		await writeIn(cwd, ".omp/settings.json", JSON.stringify({ task: { maxConcurrency: 4 } }));
		const project = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });
		expect(cfgTaskMaxConcurrency.get(project)).toBe(4);
		const overlayFile = path.join(cwd, "overlay.yml");
		await writeFile(overlayFile, "task:\n  maxConcurrency: 6\n");
		expect(cfgTaskMaxConcurrency.get(await Settings.loadIsolated({ cwd, agentDir, inMemory: true, configFiles: [overlayFile] }))).toBe(6);
		// Without the user's own key the harness value shows through
		const bare = await Settings.loadIsolated({ cwd: await mkdtemp(path.join(testRoot, "bare-")), agentDir: await mkdtemp(path.join(testRoot, "bare-agent-")), inMemory: true });
		expect(cfgAsyncMaxJobs.get(bare)).toBe(7);
		expect(cfgAsyncMaxJobs.provenance(bare)).toBe("harness");

		delete process.env[HARNESS_DIR_ENV];
		const stock = await Settings.loadIsolated({ cwd: await mkdtemp(path.join(testRoot, "stock-")), agentDir });
		expect(cfgTaskMaxConcurrency.get(stock)).toBe(cfgTaskMaxConcurrency.default);
		expect(cfgTaskMaxConcurrency.provenance(stock)).toBe("default");
		expect(cfgAsyncMaxJobs.get(stock)).toBe(9);
	});

	test("(d) config get shows the effective value and config set writes the user's config.yml, never the harness file", async () => {
		const configAgentDir = await mkdtemp(path.join(testRoot, "config-agent-"));
		process.env[HARNESS_DIR_ENV] = harness;
		setAgentDir(configAgentDir);
		const printed: string[] = [];
		const log = spyOn(console, "log").mockImplementation((...args: unknown[]) => {
			printed.push(args.map(String).join(" "));
		});
		const lastValue = () => JSON.parse(printed.at(-1) ?? "{}").value;
		try {
			resetSettingsForTest();
			await runConfigCommand({ action: "get", key: "task.maxConcurrency", flags: { json: true } });
			expect(lastValue()).toBe(3);
			await runConfigCommand({ action: "set", key: "task.maxConcurrency", value: "5", flags: { json: true } });
			expect(lastValue()).toBe(5);
			await runConfigCommand({ action: "get", key: "task.maxConcurrency", flags: { json: true } });
			expect(lastValue()).toBe(5);
		} finally {
			log.mockRestore();
			resetSettingsForTest();
			setAgentDir(agentDir);
		}
		expect(Bun.YAML.parse(readFileSync(path.join(configAgentDir, "config.yml"), "utf8"))).toEqual({ task: { maxConcurrency: 5 } });
		expect(readFileSync(path.join(harness, "agent/config.yml"), "utf8")).toBe(harnessConfig);
		// The written value now wins over the harness default on a fresh load
		const reloaded = await Settings.loadIsolated({ cwd: await mkdtemp(path.join(testRoot, "config-cwd-")), agentDir: configAgentDir });
		expect(cfgTaskMaxConcurrency.get(reloaded)).toBe(5);
		expect(cfgTaskMaxConcurrency.provenance(reloaded)).toBe("global");
	});

	test("(e) the TUI/CLI settings path (Settings.init) sees the harness defaults", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "cli-"));
		try {
			process.env[HARNESS_DIR_ENV] = harness;
			resetSettingsForTest();
			const cli = await Settings.init({ cwd, agentDir });
			expect(cfgTaskMaxConcurrency.get(cli)).toBe(3);
			expect(cfgAsyncMaxJobs.get(cli)).toBe(9);

			delete process.env[HARNESS_DIR_ENV];
			resetSettingsForTest();
			const stock = await Settings.init({ cwd, agentDir });
			expect(cfgTaskMaxConcurrency.get(stock)).toBe(cfgTaskMaxConcurrency.default);
		} finally {
			resetSettingsForTest();
		}
	});
});
