import { AsyncLocalStorage } from "node:async_hooks";
import type { Settings } from "./settings";

// This module must not import settings or capability registration at runtime:
// discovery and settings initialization both need the ambient session context.
const activeSettingsScope = new AsyncLocalStorage<Settings>();

/** Bind session settings without changing unrelated request/admission async scopes. */
export function withActiveSettings<T>(instance: Settings | undefined, fn: () => T): T {
	return instance ? activeSettingsScope.run(instance, fn) : fn();
}

/** The current session's settings, without a process-singleton fallback. */
export function getActiveSettings(): Settings | undefined {
	return activeSettingsScope.getStore();
}

let processSettings: Settings | undefined;

/** Record the process-wide `Settings.init` instance (owned by `settings.ts`). */
export function setProcessSettings(instance: Settings | undefined): void {
	processSettings = instance;
}

/**
 * The folder in scope is not trusted (`--untrusted-project`): the current session's settings,
 * else the process singleton, leave out the project layer. Gates project-local code that
 * discovery would otherwise run (plugins, interpreters) and the folder's skills, slash
 * commands and prompt templates.
 */
export function isUntrustedProjectScope(): boolean {
	return (activeSettingsScope.getStore() ?? processSettings)?.ignoresProjectSettings === true;
}
