/**
 * Ombra harness layer: an app-owned set of rules, agents, extensions, skills and settings defaults
 * that Ombra ships with its runtime and names through `OMBRA_HARNESS_DIR` for every engine, TUI
 * and CLI launch. The layer sits below the user's own `~/.omp/agent` (or active profile) and the
 * project, and above omp's bundled items and registry defaults; nothing is ever written into it.
 *
 * Layout of the root (it mirrors `harness/profile`):
 *   - `agent/AGENTS.md`           user-global context file, rendered before the user's own AGENTS.md
 *   - `agent/agents/*.md`         task agents, after user/project/plugin agents and before bundled ones
 *   - `agent/rules/*.md`          rules; a same-name project or user rule wins, bundled rules lose
 *   - `agent/extensions/<name>/`  extension modules; a same-name user or project extension wins
 *   - `agent/config.yml`          settings defaults, just above registry defaults and below `config.yml`
 *   - `skills/<name>/SKILL.md`    skills; any same-name authored skill wins
 *
 * With the variable unset or empty every helper returns `undefined` and omp behaves as stock.
 * The harness is trusted regardless of the folder: an untrusted project drops only project items.
 */
import * as path from "node:path";
import { logger } from "@oh-my-pi/pi-utils";

/** Environment variable naming the absolute harness root. */
export const HARNESS_DIR_ENV = "OMBRA_HARNESS_DIR";

/** Capability provider id of harness items (skills, extension modules, context files). */
export const HARNESS_PROVIDER_ID = "ombra-harness";

/** Last relative value reported, so a misconfigured launch warns once instead of on every lookup. */
let warnedRelativeRoot: string | undefined;

/** Absolute harness root from `OMBRA_HARNESS_DIR`, or `undefined` when unset, empty or relative. */
export function getHarnessRoot(): string | undefined {
	const raw = process.env[HARNESS_DIR_ENV];
	if (!raw) return undefined;
	if (!path.isAbsolute(raw)) {
		if (warnedRelativeRoot !== raw) {
			warnedRelativeRoot = raw;
			logger.warn(`${HARNESS_DIR_ENV} must be an absolute path; ignoring the harness layer`, { value: raw });
		}
		return undefined;
	}
	return path.normalize(raw);
}

function harnessPath(...segments: string[]): string | undefined {
	const root = getHarnessRoot();
	return root === undefined ? undefined : path.join(root, ...segments);
}

/** `<root>/skills`: one `<name>/SKILL.md` per skill. */
export function getHarnessSkillsDir(): string | undefined {
	return harnessPath("skills");
}

/** `<root>/agent/agents`: task agent definitions (`*.md`). */
export function getHarnessAgentsDir(): string | undefined {
	return harnessPath("agent", "agents");
}

/** `<root>/agent/extensions`: one extension module per entry. */
export function getHarnessExtensionsDir(): string | undefined {
	return harnessPath("agent", "extensions");
}

/** `<root>/agent/rules`: rule files (`*.md`, frontmatter `description`, `alwaysApply`, `condition`). */
export function getHarnessRulesDir(): string | undefined {
	return harnessPath("agent", "rules");
}

/** `<root>/agent/AGENTS.md`: the harness rules, a user-global context file. */
export function getHarnessContextFile(): string | undefined {
	return harnessPath("agent", "AGENTS.md");
}

/** `<root>/agent/config.yml`: read-only settings defaults. */
export function getHarnessConfigFile(): string | undefined {
	return harnessPath("agent", "config.yml");
}
