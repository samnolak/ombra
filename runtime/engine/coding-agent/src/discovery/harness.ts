/**
 * Ombra harness provider (`OMBRA_HARNESS_DIR`, see `harness-layer.ts`).
 *
 * Harness items are user-level (app-owned, trusted in any folder) and rank below every other
 * source, so a same-name project or user item always wins the first-wins dedup:
 *   - skills, extension modules and rules register just above the managed (auto-learn) skills
 *     and the bundled default rules, so bundled rules of the same name lose to the harness ones;
 *   - the AGENTS.md is an additive user-global context file registered above the native provider,
 *     so it is listed, and rendered, before the user's own `~/.omp/agent/AGENTS.md`: the user's
 *     rules come later and win. It shares the user-global `contextFiles.maxBytes` chain.
 * Task agents are read by `task/discovery.ts`; settings defaults by `config/settings.ts`.
 */
import { registerProvider } from "../capability";
import { type ContextFile, contextFileCapability } from "../capability/context-file";
import { type ExtensionModule, extensionModuleCapability } from "../capability/extension-module";
import { readFile } from "../capability/fs";
import { type Rule, ruleCapability } from "../capability/rule";
import { type Skill, skillCapability } from "../capability/skill";
import type { LoadContext, LoadResult } from "../capability/types";
import {
	getHarnessContextFile,
	getHarnessExtensionsDir,
	getHarnessRulesDir,
	getHarnessSkillsDir,
	HARNESS_PROVIDER_ID,
} from "../harness-layer";
import {
	createSourceMeta,
	discoverExtensionModulePaths,
	discoverRuleFromMarkdown,
	getExtensionNameFromPath,
	loadFilesFromDir,
	scanSkillsFromDir,
} from "./helpers";

const DISPLAY_NAME = "Ombra harness";
const DESCRIPTION = "App-owned harness shipped with Ombra (OMBRA_HARNESS_DIR)";
/** Below every authored source; above managed skills (5) and bundled rules (1). */
const PRIORITY = 6;
/** Above the native provider (100): the harness rules render before the user's own AGENTS.md. */
const CONTEXT_FILE_PRIORITY = 101;

registerProvider<Skill>(skillCapability.id, {
	id: HARNESS_PROVIDER_ID,
	displayName: DISPLAY_NAME,
	description: DESCRIPTION,
	priority: PRIORITY,
	load: async (ctx: LoadContext): Promise<LoadResult<Skill>> => {
		const dir = getHarnessSkillsDir();
		if (!dir) return { items: [] };
		return scanSkillsFromDir(ctx, { dir, providerId: HARNESS_PROVIDER_ID, level: "user", requireDescription: true });
	},
});

registerProvider<Rule>(ruleCapability.id, {
	id: HARNESS_PROVIDER_ID,
	displayName: DISPLAY_NAME,
	description: DESCRIPTION,
	priority: PRIORITY,
	load: async (ctx: LoadContext): Promise<LoadResult<Rule>> => {
		const dir = getHarnessRulesDir();
		if (!dir) return { items: [] };
		return loadFilesFromDir<Rule>(ctx, dir, HARNESS_PROVIDER_ID, "user", {
			extensions: ["md", "mdc"],
			transform: (name, content, filePath, source) =>
				discoverRuleFromMarkdown(name, content, filePath, source, { stripNamePattern: /\.(md|mdc)$/ }),
		});
	},
});

registerProvider<ExtensionModule>(extensionModuleCapability.id, {
	id: HARNESS_PROVIDER_ID,
	displayName: DISPLAY_NAME,
	description: DESCRIPTION,
	priority: PRIORITY,
	load: async (ctx: LoadContext): Promise<LoadResult<ExtensionModule>> => {
		const dir = getHarnessExtensionsDir();
		if (!dir) return { items: [] };
		const items = (await discoverExtensionModulePaths(ctx, dir)).map(extPath => ({
			name: getExtensionNameFromPath(extPath),
			path: extPath,
			level: "user" as const,
			_source: createSourceMeta(HARNESS_PROVIDER_ID, extPath, "user"),
		}));
		return { items };
	},
});

registerProvider<ContextFile>(contextFileCapability.id, {
	id: HARNESS_PROVIDER_ID,
	displayName: DISPLAY_NAME,
	description: "Harness rules (agent/AGENTS.md), rendered before the user's own AGENTS.md",
	priority: CONTEXT_FILE_PRIORITY,
	load: async (): Promise<LoadResult<ContextFile>> => {
		const filePath = getHarnessContextFile();
		const content = filePath ? await readFile(filePath) : null;
		if (!filePath || !content) return { items: [] };
		return {
			items: [
				{
					path: filePath,
					content,
					level: "user",
					// Its own slot: it never takes the place of the user's AGENTS.md.
					additive: true,
					_source: createSourceMeta(HARNESS_PROVIDER_ID, filePath, "user"),
				},
			],
		};
	},
});
