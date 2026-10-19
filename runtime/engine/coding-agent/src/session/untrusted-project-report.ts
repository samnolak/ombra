import type { AgentSession } from "./agent-session";

/** One thing an untrusted folder would have contributed and `--untrusted-project` kept from running. */
export interface UntrustedProjectItem {
	kind: "mcp" | "extension" | "hook" | "tool" | "command" | "skill" | "agent" | "plugin" | "lsp" | "settings";
	/**
	 * MCP server name; extension/hook/tool path (relative to cwd when inside it); command `/name`
	 * (command modules, markdown slash commands, prompt templates); skill or subagent name; plugin id;
	 * lsp `server (command)`; settings `<file>: key1, key2`.
	 */
	name: string;
	/** Absolute path of the file that declares (or is) the item. */
	source: string;
}

/** `get_state.untrustedProject`: present only for sessions opened with `--untrusted-project`. */
export interface UntrustedProjectReport {
	items: UntrustedProjectItem[];
}

// Keyed by the logical session object, which survives `new_session`/`switch_session`.
const reports = new WeakMap<AgentSession, UntrustedProjectReport>();

export function setUntrustedProjectReport(session: AgentSession, report: UntrustedProjectReport): void {
	reports.set(session, report);
}

/** The report computed when the session was created, or `null` when its folder was not restricted. */
export function getUntrustedProjectReport(session: AgentSession): UntrustedProjectReport | null {
	return reports.get(session) ?? null;
}
