/**
 * What an untrusted folder would have contributed to a session, computed with the engine's own
 * discovery so the report matches what would have loaded. Paths and JSON only: nothing is
 * imported, spawned or connected.
 */
import * as os from "node:os";
import * as path from "node:path";
import { type Hook, hookCapability } from "../capability/hook";
import { type Settings as SettingsFile, settingsCapability } from "../capability/settings";
import { type SlashCommand, slashCommandCapability } from "../capability/slash-command";
import { loadCapability } from "../discovery";
import { listClaudePluginRoots, resolveActiveProjectRegistryPath } from "../discovery/helpers";
import { discoverCustomCommands } from "../extensibility/custom-commands";
import { discoverCustomToolPaths } from "../extensibility/custom-tools";
import { getEnabledPlugins } from "../extensibility/plugins/loader";
import { cfgDisabledExtensions, cfgSkills } from "../extensibility/settings";
import { loadSkills } from "../extensibility/skills";
import { findRootMarker, findServerConfigFile, loadConfig } from "../lsp/config";
import { loadAllMCPConfigs } from "../mcp/config";
import { discoverSessionExtensionPaths } from "../sdk";
import type { UntrustedProjectItem } from "../session/untrusted-project-report";
import { discoverAgents } from "../task/discovery";
import { getNeutralProjectDir } from "./neutral-project-dir";
import { loadPromptTemplates } from "./prompt-templates";
import { Settings } from "./settings";
import { withActiveSettings } from "./settings-context";

/**
 * Everything the folder (cwd, the ancestors discovery walks, its plugin roots) would contribute
 * to a session that `--untrusted-project` suppresses. `settings` is the session's own (untrusted)
 * settings; the folder's view is rebuilt from a throwaway in-memory instance that reads its project layer.
 */
export async function collectUntrustedProjectItems(
	cwd: string,
	agentDir: string,
	settings: Settings,
): Promise<UntrustedProjectItem[]> {
	const root = path.resolve(cwd);
	const display = (file: string): string => {
		const relative = path.relative(root, file);
		return relative && !relative.startsWith("..") && !path.isAbsolute(relative) ? relative : path.resolve(file);
	};
	// The folder's view: its own settings layer (an invalid one still shows everything but its `extensions` key).
	const folderSettings = await Settings.loadIsolated({ cwd: root, agentDir, inMemory: true }).catch(() =>
		Settings.loadIsolated({ cwd: getNeutralProjectDir(), agentDir, inMemory: true }),
	);
	const userExtensionPaths = await withActiveSettings(settings, () =>
		discoverSessionExtensionPaths({}, root, settings),
	);

	const items: UntrustedProjectItem[] = [];
	const seen = new Set<string>();
	const add = (item: UntrustedProjectItem): void => {
		const key = `${item.kind}\0${item.name}\0${item.source}`;
		if (seen.has(key)) return;
		seen.add(key);
		items.push(item);
	};

	await withActiveSettings(folderSettings, async () => {
		const [extensionPaths, hooks, tools, commands, mcp, plugins, pluginRoots, registryPath, settingsFiles, skills, slashCommands, templates, agents] =
			await Promise.all([
				discoverSessionExtensionPaths({}, root, folderSettings),
				loadCapability<Hook>(hookCapability.id, { cwd: root }),
				discoverCustomToolPaths([], root, agentDir),
				discoverCustomCommands({ cwd: root, agentDir }),
				loadAllMCPConfigs(root, { enableProjectConfig: true, filterExa: false }),
				getEnabledPlugins(root, { includeProject: true }),
				listClaudePluginRoots(os.homedir(), root),
				resolveActiveProjectRegistryPath(root),
				loadCapability<SettingsFile>(settingsCapability.id, { cwd: root }),
				loadSkills({ ...cfgSkills.get(folderSettings), cwd: root, disabledExtensions: cfgDisabledExtensions.get(folderSettings) }),
				loadCapability<SlashCommand>(slashCommandCapability.id, { cwd: root }),
				loadPromptTemplates({ cwd: root, agentDir }),
				discoverAgents(root),
			]);

		// Extensions and hooks: whatever discovery finds here beyond what the user's own setup finds in an empty folder.
		const userPaths = new Set(userExtensionPaths.map(file => path.resolve(root, file)));
		const hookPaths = new Set(
			hooks.items.filter(hook => hook.level === "project").map(hook => path.resolve(hook.path)),
		);
		for (const file of extensionPaths.map(extensionPath => path.resolve(root, extensionPath))) {
			if (userPaths.has(file)) continue;
			add({ kind: hookPaths.has(file) ? "hook" : "extension", name: display(file), source: file });
		}
		for (const tool of tools) {
			if (tool.source?.level !== "project") continue;
			const file = path.resolve(root, tool.path);
			add({ kind: "tool", name: display(file), source: file });
		}
		for (const command of commands.paths) {
			if (command.source !== "project") continue;
			add({ kind: "command", name: `/${path.basename(path.dirname(command.path))}`, source: command.path });
		}
		// Skills, markdown slash commands, prompt templates and subagent definitions: what the folder would add to the agent.
		for (const skill of skills.skills) {
			if (skill._source?.level === "project") add({ kind: "skill", name: skill.name, source: skill.filePath });
		}
		for (const command of slashCommands.items) {
			if (command.level === "project") add({ kind: "command", name: `/${command.name}`, source: command.path });
		}
		for (const template of templates) {
			if (template.filePath && template.source.startsWith("(project")) {
				add({ kind: "command", name: `/${template.name}`, source: template.filePath });
			}
		}
		for (const agent of agents.agents) {
			if (agent.source === "project" && agent.filePath) add({ kind: "agent", name: agent.name, source: agent.filePath });
		}
		for (const [name, source] of Object.entries(mcp.sources)) {
			if (source.level === "project") add({ kind: "mcp", name, source: source.path });
		}
		for (const plugin of plugins) {
			if (plugin.scope !== "project") continue;
			add({ kind: "plugin", name: plugin.name, source: path.join(plugin.path, "package.json") });
		}
		for (const pluginRoot of pluginRoots.roots) {
			if (pluginRoot.scope !== "project" || !registryPath) continue;
			add({ kind: "plugin", name: pluginRoot.id, source: registryPath });
		}
		for (const file of settingsFiles.items) {
			const keys = Object.keys(file.data);
			if (file.level !== "project" || keys.length === 0) continue;
			add({ kind: "settings", name: `${display(file.path)}: ${keys.join(", ")}`, source: file.path });
		}
		// Every language server is off under the flag; report each one the normal config would enable here.
		for (const [name, server] of Object.entries(loadConfig(root).servers)) {
			const source = findServerConfigFile(root, name) ?? findRootMarker(root, server.rootMarkers);
			if (source) add({ kind: "lsp", name: `${name} (${server.resolvedCommand ?? server.command})`, source });
		}
	});
	return items;
}
