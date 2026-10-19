import { clearCache as clearFsCache } from "../capability/fs";
import type { Settings } from "../config/settings";
import type { AgentSession } from "../session/agent-session";
import type { MCPLoadResult, MCPManager } from "./manager";
import { cfgMcpEnableProjectConfig } from "./settings";

/**
 * Reconnect every configured MCP server and rebind the session's MCP tools.
 *
 * Disconnects all live connections, rediscovers `.mcp.json` configs, and
 * calls `session.refreshMCPTools(...)` so config edits take effect without a
 * restart. Discovery options are derived from settings so the reload honors
 * the same opt-outs as startup — notably `mcp.enableProjectConfig: false`,
 * which must keep project `.mcp.json` servers from being started on reload.
 */
export async function reloadSessionMcpServers(
	manager: MCPManager,
	session: Pick<AgentSession, "setMCPPromptCommands" | "getEvalPreludes" | "effectiveExtensionRoots" | "refreshMCPTools">,
	settings: Settings,
): Promise<MCPLoadResult> {
	await manager.disconnectAll();
	// Prompt enrichment is asynchronous. Clear commands before rediscovery so
	// removed/disabled servers cannot leave stale `/server:prompt` entries;
	// newly loaded prompts repopulate them through the manager callback.
	session.setMCPPromptCommands([]);
	// External edits to mcp.json (not via writeMCPConfigFile) otherwise
	// keep stale env/command after reload.
	clearFsCache();

	// Rediscover and connect, mirroring startup's discovery filters.
	const result = await manager.discoverAndConnect({
		enableProjectConfig: cfgMcpEnableProjectConfig.get(settings),
		filterExa: true,
		filterBrowser: session.getEvalPreludes().some(definition => definition.name === "browser"),
		extensionRoots: session.effectiveExtensionRoots,
	});
	await session.refreshMCPTools(manager.getTools());
	return result;
}
