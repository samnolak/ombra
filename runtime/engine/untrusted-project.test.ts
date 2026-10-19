import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { existsSync, realpathSync } from "node:fs";
import { chmod, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { getAgentDir, setAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { resetSettingsForTest, Settings } from "./coding-agent/src/config/settings";
import { isUntrustedProjectScope, withActiveSettings } from "./coding-agent/src/config/settings-context";
import { getAdapterConfigs } from "./coding-agent/src/dap/config";
import { getPreloadedPluginRoots, listClaudePluginRoots, preloadLaunchPluginRoots } from "./coding-agent/src/discovery/helpers";
import { resolveIdaRuntime } from "./coding-agent/src/ida/runtime";
import type { ToolSession } from "./coding-agent/src/tools";
import { checkPythonKernelAvailability } from "./coding-agent/src/eval/py/kernel";
import { executeBash } from "./coding-agent/src/exec/bash-executor";
import { getConfig, resolveCommand } from "./coding-agent/src/lsp/config";
import { createAgentSession } from "./coding-agent/src/sdk";
import { SessionManager } from "./coding-agent/src/session/session-manager";
import { enumeratePythonRuntimes, resolveVenvPath } from "./coding-agent/src/eval/py/runtime";
import { getEnabledPlugins } from "./coding-agent/src/extensibility/plugins/loader";
import { cfgMcpEnableProjectConfig, cfgMcpNotifications } from "./coding-agent/src/mcp/settings";
import { getActiveSkills } from "./coding-agent/src/extensibility/skills";
import { getUntrustedProjectReport } from "./coding-agent/src/session/untrusted-project-report";
import { buildAvailableSlashCommands } from "./coding-agent/src/slash-commands/available-commands";
import { discoverAgents } from "./coding-agent/src/task/discovery";
import { createEngineSession, type EngineSessionResult, initEngineProcessSettings, parseEngineSessionArgs } from "./session-factory";

// Ombra's workspace trust: a chat in a folder the user has not trusted opens with --untrusted-project, and none of
// the folder's own code may run in the shared engine (its settings, MCP servers, custom tools, extensions, hooks,
// plugins, interpreters). The user's own setup still loads.
describe("untrusted project folder", () => {
	// Real path: discovery reports resolved paths, and macOS tmp is a symlink.
	const testRoot = path.join(realpathSync(os.tmpdir()), "ombra-tests", `run-${process.pid}`, "engine-untrusted-project");
	const agentDir = path.join(testRoot, "user-agent");
	const previousAgentDir = getAgentDir();
	const previousAgentDirEnv = process.env.PI_CODING_AGENT_DIR;
	beforeAll(async () => {
		await mkdir(path.join(agentDir, "extensions"), { recursive: true });
		setAgentDir(agentDir);
	});
	afterAll(async () => {
		setAgentDir(previousAgentDir);
		if (previousAgentDirEnv === undefined) delete process.env.PI_CODING_AGENT_DIR;
		await rm(testRoot, { recursive: true, force: true });
	});

	const probeIn = (cwd: string) => {
		const marker = (name: string) => path.join(cwd, `${name}.ran`);
		const probe = (name: string, exported: string) =>
			`import { writeFileSync } from "node:fs";\nwriteFileSync(${JSON.stringify(marker(name))}, "1");\n${exported}\n`;
		return { marker, probe };
	};
	const writeIn = async (cwd: string, file: string, content: string) => {
		await mkdir(path.dirname(path.join(cwd, file)), { recursive: true });
		await writeFile(path.join(cwd, file), content);
	};
	// The model comes from an explicit extension outside the project's own config (explicit paths always load)
	const providerFlags = async (name: string) => {
		const provider = path.join(testRoot, `provider-${name}.ts`);
		await writeFile(provider, `export default api => api.registerProvider("untrusted-probe", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-probe-key", api: "openai-completions",
			models: [{ id: "m", name: "m", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }],
		});`);
		return ["--extension", provider, "--model", "untrusted-probe/m", "--no-skills", "--no-rules", "--no-prewalk", "--no-session"];
	};

	test("--untrusted-project turns off language servers but keeps the user's extensions", () => {
		const parsed = parseEngineSessionArgs(["--untrusted-project"]);
		expect(parsed.untrustedProject).toBe(true);
		expect(parsed.noExtensions).toBeUndefined();
		expect(parsed.noLsp).toBe(true);
		expect(parseEngineSessionArgs([]).untrustedProject).toBeUndefined();
	});

	test("the project settings layer is left out, the user's stays", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "settings-"));
		await mkdir(path.join(cwd, ".omp"), { recursive: true });
		await writeFile(path.join(cwd, ".omp", "settings.json"), JSON.stringify({ mcp: { notifications: true } }));
		const trusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });
		const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });
		expect(cfgMcpNotifications.get(trusted)).toBe(true);
		expect(cfgMcpNotifications.get(untrusted)).toBe(false);
		expect(trusted.ignoresProjectSettings).toBe(false);
		expect(untrusted.ignoresProjectSettings).toBe(true);
		// Child (overlay) settings of subagents inherit the folder's trust
		expect(untrusted.overlay().ignoresProjectSettings).toBe(true);
		// A clone for another folder (cwd change) keeps the folder untrusted
		expect(cfgMcpNotifications.get(await untrusted.cloneForCwd(cwd))).toBe(false);
	});

	test("a project layer pinned to the launch folder is never read for another folder", async () => {
		const launch = await mkdtemp(path.join(testRoot, "launch-"));
		const other = await mkdtemp(path.join(testRoot, "other-"));
		await writeIn(other, ".omp/settings.json", JSON.stringify({ mcp: { notifications: true } }));
		const pinnedAgentDir = await mkdtemp(path.join(testRoot, "pinned-agent-"));

		const pinned = await Settings.loadIsolated({ cwd: launch, agentDir: pinnedAgentDir, projectRoot: launch });
		await pinned.reloadForCwd(other);
		expect(pinned.getCwd()).toBe(other);
		expect(cfgMcpNotifications.get(pinned)).toBe(false);
		expect(cfgMcpNotifications.get(await pinned.cloneForCwd(other))).toBe(false);

		// Control: without the pin a move takes on the destination's project layer (CLI behaviour)
		const unpinned = await Settings.loadIsolated({ cwd: launch, agentDir: pinnedAgentDir });
		await unpinned.reloadForCwd(other);
		expect(cfgMcpNotifications.get(unpinned)).toBe(true);
	});

	test("an untrusted folder's .venv is never picked as the Python runtime", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "venv-"));
		const venv = path.join(cwd, ".venv");
		await writeIn(cwd, ".venv/bin/python", "#!/bin/sh\nexit 0\n");
		await chmod(path.join(venv, "bin", "python"), 0o755);
		const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });
		const trusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });
		const baseEnv = { PATH: process.env.PATH };

		const untrustedVenv = withActiveSettings(untrusted, () => resolveVenvPath(cwd));
		const untrustedRuntimes = withActiveSettings(untrusted, () => enumeratePythonRuntimes(cwd, baseEnv));
		expect(untrustedVenv === venv).toBe(false);
		expect(untrustedRuntimes.some(runtime => runtime.pythonPath.startsWith(venv))).toBe(false);
		// Control: a trusted folder's .venv is picked unless the user's environment already names one
		if (!process.env.VIRTUAL_ENV && !process.env.CONDA_PREFIX) {
			expect(withActiveSettings(trusted, () => resolveVenvPath(cwd))).toBe(venv);
		}
	});

	test("an untrusted folder's plugins are not enabled", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "plugins-"));
		await writeIn(cwd, ".omp/plugins/package.json", JSON.stringify({ dependencies: { "probe-plugin": "1.0.0" } }));
		await writeIn(cwd, ".omp/plugins/node_modules/probe-plugin/package.json",
			JSON.stringify({ name: "probe-plugin", version: "1.0.0", omp: {} }));
		const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });
		const trusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });

		const untrustedPlugins = await withActiveSettings(untrusted, () => getEnabledPlugins(cwd));
		const trustedPlugins = await withActiveSettings(trusted, () => getEnabledPlugins(cwd));
		expect(untrustedPlugins.some(plugin => plugin.scope === "project")).toBe(false);
		expect(trustedPlugins.some(plugin => plugin.name === "probe-plugin" && plugin.scope === "project")).toBe(true);
	});

	test("a session imports none of the folder's code, keeps its MCP servers and settings off, and loads the user's", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "session-"));
		const { marker, probe } = probeIn(cwd);
		await writeIn(cwd, ".omp/tools/probe.ts", probe("tool", "export default () => [];"));
		await writeIn(cwd, ".omp/extensions/probe.ts", probe("extension", "export default () => {};"));
		await writeIn(cwd, ".omp/hooks/pre/probe.ts", probe("hook", "export default () => {};"));
		await writeIn(cwd, ".omp/settings.json", JSON.stringify({ mcp: { notifications: true } }));
		// The user's own ambient extension
		const userMarker = path.join(cwd, "user-extension.ran");
		await writeFile(path.join(agentDir, "extensions", `user-${path.basename(cwd)}.ts`),
			`import { writeFileSync } from "node:fs";\nwriteFileSync(${JSON.stringify(userMarker)}, "1");\nexport default () => {};\n`);
		const flags = await providerFlags(path.basename(cwd));

		const untrusted = await createEngineSession(cwd, [...flags, "--untrusted-project"]);
		try {
			expect(cfgMcpEnableProjectConfig.get(untrusted.session.settings)).toBe(false);
			expect(cfgMcpNotifications.get(untrusted.session.settings)).toBe(false);
			// Custom tools, extensions and hooks are imported while the session is created, which has finished here
			for (const name of ["tool", "extension", "hook"]) expect(existsSync(marker(name))).toBe(false);
			expect(existsSync(userMarker)).toBe(true);
			expect(getUntrustedProjectReport(untrusted.session)).not.toBeNull();
			// The user's extensions are told the folder is untrusted (upstream Pi's gate for project-local config)
			expect(untrusted.session.extensionRunner?.createContext().isProjectTrusted()).toBe(false);
		} finally { await untrusted.runInContext(() => untrusted.session.dispose()); }

		// Control: without the flag the same folder's custom tool is imported (--no-extensions alone never stopped it)
		const trusted = await createEngineSession(cwd, [...flags, "--no-extensions", "--no-lsp"]);
		try {
			expect(existsSync(marker("tool"))).toBe(true);
			expect(cfgMcpEnableProjectConfig.get(trusted.session.settings)).toBe(true);
			expect(cfgMcpNotifications.get(trusted.session.settings)).toBe(true);
			// get_state.untrustedProject is null for a session opened without the flag
			expect(getUntrustedProjectReport(trusted.session)).toBeNull();
			expect(trusted.session.extensionRunner?.createContext().isProjectTrusted()).toBe(true);
		} finally { await trusted.runInContext(() => trusted.session.dispose()); }
	}, 60_000);

	test("the session reports what the folder would have run, and none of it runs", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "report-"));
		const { marker, probe } = probeIn(cwd);
		// A declaration-named extension still runs when imported
		await writeIn(cwd, ".omp/extensions/x.d.ts", probe("dts", "export default () => {};"));
		// A manifest-declared extension outside its directory, with no code under foo/
		await writeIn(cwd, ".omp/extensions/foo/package.json", JSON.stringify({ omp: { extensions: ["../../../src/util.ts"] } }));
		await writeIn(cwd, "src/util.ts", probe("manifest", "export default () => {};"));
		// A foreign provider's settings file naming a program
		await writeIn(cwd, ".gemini/settings.json", JSON.stringify({ shellPath: "./x.sh" }));
		// An MCP server and a project-local language server binary
		await writeIn(cwd, ".mcp.json", JSON.stringify({
			mcpServers: { "probe-mcp": { command: "sh", args: ["-c", `echo 1 > ${JSON.stringify(marker("mcp"))}`] } },
		}));
		await writeIn(cwd, "go.mod", "module probe\n\ngo 1.22\n");
		await writeIn(cwd, "bin/gopls", `#!/bin/sh\necho 1 > ${JSON.stringify(marker("gopls"))}\n`);
		await chmod(path.join(cwd, "bin", "gopls"), 0o755);
		// A project plugin registry pointing at an install with a tool
		const pluginRoot = path.join(cwd, "vendor", "probe-plugin");
		await writeIn(cwd, ".omp/plugins/installed_plugins.json", JSON.stringify({
			version: 2,
			plugins: { "probe@local": [{ installPath: pluginRoot, version: "1.0.0" }] },
		}));
		await writeIn(cwd, "vendor/probe-plugin/tools/plugtool.ts", probe("plugintool", "export default () => [];"));
		const flags = await providerFlags(path.basename(cwd));

		const untrusted = await createEngineSession(cwd, [...flags, "--untrusted-project"]);
		try {
			for (const name of ["dts", "manifest", "mcp", "gopls", "plugintool"]) expect(existsSync(marker(name))).toBe(false);
			const report = getUntrustedProjectReport(untrusted.session);
			expect(report).not.toBeNull();
			const items = report?.items ?? [];
			const has = (kind: string, name: (value: string) => boolean, source?: string) =>
				items.some(item => item.kind === kind && name(item.name) && (source === undefined || item.source === source));
			expect(has("extension", name => name === ".omp/extensions/x.d.ts", path.join(cwd, ".omp/extensions/x.d.ts"))).toBe(true);
			expect(has("extension", name => name === "src/util.ts", path.join(cwd, "src/util.ts"))).toBe(true);
			expect(has("settings", name => name === ".gemini/settings.json: shellPath", path.join(cwd, ".gemini/settings.json"))).toBe(true);
			expect(has("mcp", name => name === "probe-mcp", path.join(cwd, ".mcp.json"))).toBe(true);
			expect(has("lsp", name => name === `gopls (${path.join(cwd, "bin", "gopls")})`, path.join(cwd, "go.mod"))).toBe(true);
			expect(has("plugin", name => name === "probe@local", path.join(cwd, ".omp/plugins/installed_plugins.json"))).toBe(true);
			// Every source is absolute
			expect(items.every(item => path.isAbsolute(item.source))).toBe(true);
		} finally { await untrusted.runInContext(() => untrusted.session.dispose()); }
	}, 60_000);

	test("an untrusted folder's skills, prompt templates and slash commands are neither listed nor invocable until trusted", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "skills-"));
		const id = path.basename(cwd).toLowerCase().replace(/[^a-z0-9]+/g, "-");
		const skill = (name: string) => `---\nname: ${name}\ndescription: Probe skill ${name}\n---\nRun ./probe.sh\n`;
		await writeIn(cwd, ".omp/skills/probe-skill/SKILL.md", skill("probe-skill"));
		await writeIn(cwd, ".claude/skills/claude-probe/SKILL.md", skill("claude-probe"));
		await writeIn(cwd, ".omp/commands/probe-cmd.md", "---\ndescription: Probe command\n---\nRun the probe\n");
		await writeIn(cwd, ".omp/prompts/probe-prompt.md", "---\ndescription: Probe prompt\n---\nPrompt the probe\n");
		// AGENTS.md stays: context files are prompt text the user already accepts in any folder
		await writeIn(cwd, "AGENTS.md", "Be brief.\n");
		// The user's own skill still loads
		const userSkill = `user-${id}`;
		await writeIn(agentDir, `skills/${userSkill}/SKILL.md`, skill(userSkill));
		const flags = (await providerFlags(path.basename(cwd))).filter(flag => flag !== "--no-skills");
		const names = async (created: EngineSessionResult) => {
			const available = await created.runInContext(() => buildAvailableSlashCommands(created.session));
			return {
				skills: created.session.skills.map(item => item.name),
				active: created.runInContext(() => getActiveSkills().map(item => item.name)),
				commands: available.map(command => command.name),
				files: created.session.slashCommands.map(command => command.name),
				prompts: created.session.promptTemplates.map(template => template.name),
			};
		};

		const untrusted = await createEngineSession(cwd, [...flags, "--untrusted-project"]);
		try {
			const seen = await names(untrusted);
			for (const list of [seen.skills, seen.active]) {
				expect(list).not.toContain("probe-skill");
				expect(list).not.toContain("claude-probe");
				expect(list).toContain(userSkill);
			}
			expect(seen.commands).not.toContain("skill:probe-skill");
			expect(seen.commands).not.toContain("probe-cmd");
			expect(seen.commands).toContain(`skill:${userSkill}`);
			expect(seen.files).not.toContain("probe-cmd");
			expect(seen.prompts).not.toContain("probe-prompt");
			// The context file is still read
			expect(untrusted.session.systemPrompt.join("\n")).toContain("Be brief.");

			const items = getUntrustedProjectReport(untrusted.session)?.items ?? [];
			const has = (kind: string, name: string, source: string) =>
				items.some(item => item.kind === kind && item.name === name && item.source === source);
			expect(has("skill", "probe-skill", path.join(cwd, ".omp/skills/probe-skill/SKILL.md"))).toBe(true);
			expect(has("skill", "claude-probe", path.join(cwd, ".claude/skills/claude-probe/SKILL.md"))).toBe(true);
			expect(has("command", "/probe-cmd", path.join(cwd, ".omp/commands/probe-cmd.md"))).toBe(true);
			expect(has("command", "/probe-prompt", path.join(cwd, ".omp/prompts/probe-prompt.md"))).toBe(true);
			// The user's own skill is not the folder's
			expect(items.some(item => item.name === userSkill)).toBe(false);
		} finally { await untrusted.runInContext(() => untrusted.session.dispose()); }

		// Trust and restart: the same folder's skills, commands and prompt templates load
		const trusted = await createEngineSession(cwd, [...flags, "--no-extensions", "--no-lsp"]);
		try {
			const seen = await names(trusted);
			expect(seen.skills).toContain("probe-skill");
			expect(seen.skills).toContain("claude-probe");
			expect(seen.active).toContain("probe-skill");
			expect(seen.commands).toContain("skill:probe-skill");
			expect(seen.commands).toContain("probe-cmd");
			expect(seen.files).toContain("probe-cmd");
			expect(seen.prompts).toContain("probe-prompt");
		} finally {
			await trusted.runInContext(() => trusted.session.dispose());
			await rm(path.join(agentDir, "skills", userSkill), { recursive: true, force: true });
		}
	}, 60_000);

	test("an untrusted folder's subagent definitions are neither offered nor spawnable until trusted", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "agents-"));
		const agent = (name: string) => `---\nname: ${name}\ndescription: Probe agent ${name}\ntools: [bash]\n---\nRun ./probe.sh\n`;
		await writeIn(cwd, ".omp/agents/probe-agent.md", agent("probe-agent"));
		// A project agent named like one the folder does not own (bundled `scout`) does not replace it
		await writeIn(cwd, ".omp/agents/scout.md", agent("scout"));
		const flags = await providerFlags(path.basename(cwd));
		const seen = (created: EngineSessionResult) => created.runInContext(async () => {
			const { agents } = await discoverAgents(cwd);
			return {
				agents: agents.map(item => item.name),
				scout: agents.find(item => item.name === "scout")?.description,
				offered: String(created.session.getToolByName("task")?.description ?? ""),
			};
		});

		const untrusted = await createEngineSession(cwd, [...flags, "--untrusted-project"]);
		try {
			const view = await seen(untrusted);
			expect(view.agents).not.toContain("probe-agent");
			expect(view.offered).not.toContain("probe-agent");
			expect(view.scout).not.toBe("Probe agent scout");
			expect(view.scout).toBeDefined();
			const items = getUntrustedProjectReport(untrusted.session)?.items ?? [];
			expect(items.some(item => item.kind === "agent" && item.name === "probe-agent"
				&& item.source === path.join(cwd, ".omp/agents/probe-agent.md"))).toBe(true);
		} finally { await untrusted.runInContext(() => untrusted.session.dispose()); }

		// Trust and restart: the folder's agents load (the same folder, after an untrusted chat cached its view)
		const trusted = await createEngineSession(cwd, [...flags, "--no-extensions", "--no-lsp"]);
		try {
			const view = await seen(trusted);
			expect(view.agents).toContain("probe-agent");
			expect(view.offered).toContain("probe-agent");
			expect(view.scout).toBe("Probe agent scout");
		} finally { await trusted.runInContext(() => trusted.session.dispose()); }
	}, 60_000);

	test("bash never takes the shell named by a folder the process-wide settings were created in", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "shell-"));
		const { marker } = probeIn(cwd);
		// Named like bash, so the executor starts it (its rc snapshot) rather than the embedded shell
		const shell = path.join(cwd, "tools", "bash");
		await writeIn(cwd, "tools/bash", `#!/bin/sh\necho 1 > ${JSON.stringify(marker("shell"))}\nexec /bin/bash "$@"\n`);
		await chmod(shell, 0o755);
		await writeIn(cwd, ".omp/settings.json", JSON.stringify({ shellPath: shell }));
		// The lazily created process singleton read this folder's project layer (engine cwd or a /move target)
		resetSettingsForTest();
		try {
			await Settings.init({ cwd, agentDir, inMemory: true });
			const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });
			const result = await withActiveSettings(untrusted, () => executeBash("echo ok", { cwd }));
			expect(result.exitCode).toBe(0);
			expect(existsSync(marker("shell"))).toBe(false);
		} finally { resetSettingsForTest(); }
	}, 30_000);

	test("debug adapters are never read from, or resolved in, an untrusted folder", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "dap-"));
		await writeIn(cwd, ".dap.json", JSON.stringify({ adapters: { "probe-dap": { command: "./x", languages: ["c"] } } }));
		await writeIn(cwd, ".omp/dap.json", JSON.stringify({ adapters: { "probe-dap-dir": { command: "./y" } } }));
		await writeIn(cwd, "package.json", "{}");
		await writeIn(cwd, "node_modules/.bin/probe-adapter", "#!/bin/sh\n");
		await chmod(path.join(cwd, "node_modules/.bin/probe-adapter"), 0o755);
		const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });
		const trusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });

		const untrustedAdapters = withActiveSettings(untrusted, () => getAdapterConfigs(cwd));
		expect(Object.keys(untrustedAdapters)).not.toContain("probe-dap");
		expect(Object.keys(untrustedAdapters)).not.toContain("probe-dap-dir");
		expect(withActiveSettings(untrusted, () => resolveCommand("probe-adapter", cwd))).toBeNull();
		// Control: a trusted folder's adapters and local binaries are used
		expect(Object.keys(withActiveSettings(trusted, () => getAdapterConfigs(cwd)))).toContain("probe-dap");
		expect(withActiveSettings(trusted, () => resolveCommand("probe-adapter", cwd)))
			.toBe(path.join(cwd, "node_modules/.bin/probe-adapter"));
	});

	test("language servers stay off for helper sessions of an untrusted chat, and its lsp files are not read", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "lsp-"));
		await writeIn(cwd, "lsp.json", JSON.stringify({
			servers: { "probe-lsp": { command: "sh", args: [], fileTypes: [".probe"], rootMarkers: ["lsp.json"] } },
		}));
		const untrustedSettings = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });
		const trustedSettings = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });
		expect(Object.keys(withActiveSettings(untrustedSettings, () => getConfig(cwd)).servers)).not.toContain("probe-lsp");
		expect(Object.keys(withActiveSettings(trustedSettings, () => getConfig(cwd)).servers)).toContain("probe-lsp");

		const flags = await providerFlags(path.basename(cwd));
		const parent = await createEngineSession(cwd, [...flags, "--untrusted-project"]);
		const helperTools = async (settings: Settings) => {
			// A helper (security_scan coordinator, /tan) asks for read-only LSP explicitly
			const { session } = await createAgentSession({
				cwd, agentDir, settings: settings.overlay(),
				sessionManager: SessionManager.inMemory(cwd),
				modelRegistry: parent.session.modelRegistry, model: parent.session.model,
				toolNames: ["read", "lsp"], restrictToolNames: true, enableLsp: true, lspReadOnly: true,
				disableExtensionDiscovery: true, enableMCP: false, enableIrc: false, hasUI: false, bindProcessState: false,
			});
			try { return session.getActiveToolNames(); } finally { await session.dispose(); }
		};
		try {
			expect(await parent.runInContext(() => helperTools(parent.session.settings))).not.toContain("lsp");
			// Control: the same helper of a trusted chat gets its language server tool
			expect(await helperTools(trustedSettings)).toContain("lsp");
		} finally { await parent.runInContext(() => parent.session.dispose()); }
	}, 60_000);

	test("a Python runtime probed for a trusted chat is not reused for an untrusted one", async () => {
		if (process.env.VIRTUAL_ENV || process.env.CONDA_PREFIX) return;
		const cwd = await mkdtemp(path.join(testRoot, "pycache-"));
		const venv = path.join(cwd, ".venv");
		await writeIn(cwd, ".venv/bin/python", "#!/bin/sh\nexit 0\n");
		await chmod(path.join(venv, "bin", "python"), 0o755);
		const trusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });
		const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });

		const first = await withActiveSettings(trusted, () => checkPythonKernelAvailability(cwd, undefined, { forceProbe: true }));
		expect(first.ok && first.pythonPath?.startsWith(venv)).toBe(true);
		const second = await withActiveSettings(untrusted, () => checkPythonKernelAvailability(cwd, undefined, { forceProbe: true }));
		expect(second.pythonPath?.startsWith(venv) ?? false).toBe(false);
		expect(second.runtime?.pythonPath.startsWith(venv) ?? false).toBe(false);
	});

	test("an untrusted folder's .claude settings never switch a user's Claude plugin on", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "claude-plugins-"));
		const home = await mkdtemp(path.join(testRoot, "claude-home-"));
		const claudeDir = path.join(home, ".claude");
		const installPath = path.join(home, "plugin-install");
		await mkdir(installPath, { recursive: true });
		await writeIn(claudeDir, "plugins/installed_plugins.json", JSON.stringify({
			version: 2,
			plugins: { "probe@market": [{ scope: "user", installPath, version: "1.0.0" }] },
		}));
		// The user turned it off; the folder turns it back on
		await writeIn(claudeDir, "settings.json", JSON.stringify({ enabledPlugins: { "probe@market": false } }));
		await writeIn(cwd, ".claude/settings.json", JSON.stringify({ enabledPlugins: { "probe@market": true } }));
		const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });
		const trusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });
		const previous = process.env.CLAUDE_CONFIG_DIR;
		delete process.env.CLAUDE_CONFIG_DIR;
		try {
			const ids = async (settings: Settings) =>
				(await withActiveSettings(settings, () => listClaudePluginRoots(home, cwd))).roots.map(root => root.id);
			expect(await ids(untrusted)).not.toContain("probe@market");
			// Control: a trusted folder's own enabledPlugins applies
			expect(await ids(trusted)).toContain("probe@market");
		} finally {
			if (previous !== undefined) process.env.CLAUDE_CONFIG_DIR = previous;
		}
	});

	test("a launch's plugin-root preload never takes an untrusted folder's project plugin registry", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "preload-"));
		const home = await mkdtemp(path.join(testRoot, "preload-home-"));
		await writeIn(cwd, ".omp/plugins/installed_plugins.json", JSON.stringify({
			version: 2,
			plugins: { "probe@local": [{ installPath: path.join(cwd, "vendor", "probe-plugin"), version: "1.0.0" }] },
		}));
		await writeIn(cwd, "vendor/probe-plugin/dap.json", JSON.stringify({ adapters: { "probe-plugin-dap": { command: "./z" } } }));
		const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true });
		const trusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true });
		const previous = process.env.CLAUDE_CONFIG_DIR;
		delete process.env.CLAUDE_CONFIG_DIR;
		const preloaded = () => getPreloadedPluginRoots().map(root => root.id);
		try {
			// The TUI preloads before the launch settings become the process singleton
			resetSettingsForTest();
			await preloadLaunchPluginRoots(untrusted, home, cwd, undefined);
			expect(preloaded()).not.toContain("probe@local");
			expect(Object.keys(withActiveSettings(untrusted, () => getAdapterConfigs(cwd)))).not.toContain("probe-plugin-dap");
			// Control: a trusted launch preloads the folder's registry
			await preloadLaunchPluginRoots(trusted, home, cwd, undefined);
			expect(preloaded()).toContain("probe@local");
		} finally {
			await preloadLaunchPluginRoots(untrusted, home, cwd, undefined);
			if (previous !== undefined) process.env.CLAUDE_CONFIG_DIR = previous;
		}
	});

	test("an IDA interpreter probed for a trusted chat is not reused for an untrusted one", async () => {
		if (process.env.VIRTUAL_ENV || process.env.CONDA_PREFIX) return;
		const cwd = await mkdtemp(path.join(testRoot, "ida-"));
		const installDir = await mkdtemp(path.join(testRoot, "ida-install-"));
		const idalib = process.platform === "darwin" ? "libidalib.dylib" : process.platform === "win32" ? "idalib.dll" : "libidalib.so";
		await writeFile(path.join(installDir, idalib), "");
		const venv = path.join(cwd, ".venv");
		// Imports anything: the IDA probe accepts it
		await writeIn(cwd, ".venv/bin/python", "#!/bin/sh\nexit 0\n");
		await chmod(path.join(venv, "bin", "python"), 0o755);
		const overrides = { "ida.installDir": installDir };
		const trusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, overrides });
		const untrusted = await Settings.loadIsolated({ cwd, agentDir, inMemory: true, ignoreProjectSettings: true, overrides });

		const first = await resolveIdaRuntime({ cwd, settings: trusted } as ToolSession);
		expect(first.pythonPath.startsWith(venv)).toBe(true);
		const second = await resolveIdaRuntime({ cwd, settings: untrusted } as ToolSession).catch(() => undefined);
		expect(second?.pythonPath.startsWith(venv) ?? false).toBe(false);
	});

	test("the shared engine's process settings fail closed, and a trusted chat still gets its folder", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "process-"));
		const { marker, probe } = probeIn(cwd);
		await writeIn(cwd, ".omp/tools/probe.ts", probe("tool", "export default () => [];"));
		await writeIn(cwd, ".omp/settings.json", JSON.stringify({ mcp: { notifications: true } }));
		resetSettingsForTest();
		try {
			const processSettings = await initEngineProcessSettings();
			// Code running outside any chat's settings scope sees an untrusted folder
			expect(processSettings.ignoresProjectSettings).toBe(true);
			expect(isUntrustedProjectScope()).toBe(true);
			expect(withActiveSettings(processSettings, () => resolveVenvPath(cwd))).toBeUndefined();

			const trusted = await createEngineSession(cwd, [...await providerFlags(path.basename(cwd)), "--no-extensions", "--no-lsp"]);
			try {
				expect(trusted.session.settings.ignoresProjectSettings).toBe(false);
				expect(cfgMcpNotifications.get(trusted.session.settings)).toBe(true);
				expect(existsSync(marker("tool"))).toBe(true);
				expect(trusted.runInContext(() => isUntrustedProjectScope())).toBe(false);
			} finally { await trusted.runInContext(() => trusted.session.dispose()); }
		} finally { resetSettingsForTest(); }
	}, 60_000);
});
