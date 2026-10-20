import { type } from "@oh-my-pi/omptype";
import type { ExtensionFactory, ToolDefinition } from "./coding-agent/src/extensibility/extensions";
import type { MCPManager } from "./coding-agent/src/mcp/manager";
import { getRpcMcpController } from "./coding-agent/src/modes/rpc/rpc-mcp";
import type { RpcMcpServerInfo } from "./coding-agent/src/modes/rpc/rpc-types";

const connectorsSchema = type({
	action: type("'status' | 'login' | 'reconnect'").describe(
		"status: list servers; login: ask the user to sign in to `server`; reconnect: re-read mcp.json and reconnect `server` (or all)",
	),
	"server?": type("string").describe("MCP server name from mcp.json (required for login)"),
});

function describe(info: RpcMcpServerInfo): string {
	const scope = info.scope ? ` [${info.scope}]` : "";
	return `${info.name}${scope} (${info.transport}): ${info.status}, sign-in ${info.auth}, ${info.toolCount} tools${info.error ? ` — ${info.error}` : ""}`;
}

/**
 * The agent's view of the session's MCP connectors (Ombra): their status, a sign-in request the user
 * confirms in Ombra (`mcp_auth_required`, then the host's own `mcp_login`), and a reconnect that
 * re-reads mcp.json. The tool never opens a browser or sees credentials.
 */
export function createMcpConnectorsTool(
	getManager: () => MCPManager | undefined,
): ToolDefinition<typeof connectorsSchema> {
	return {
		name: "mcp_connectors",
		label: "MCP Connectors",
		description:
			"MCP servers configured for this session (mcp.json): `status` lists connection and sign-in state; `login` asks the user to sign in to a server (Ombra shows a Sign in prompt; check `status` afterwards); `reconnect` re-reads mcp.json and reconnects one server or all, after config edits.",
		parameters: connectorsSchema,
		approval: "read",
		async execute(_toolCallId, params) {
			const controller = getRpcMcpController(getManager());
			const text = (value: string, isError = false) => ({ content: [{ type: "text" as const, text: value }], isError, details: {} });
			if (!controller) return text("MCP connectors are not available in this session.", true);
			try {
				if (params.action === "login") {
					if (!params.server) return text("`server` is required for login.", true);
					const info = controller.requestSignIn(params.server);
					return text(
						`Asked the user to sign in to "${params.server}" in Ombra; it connects once they finish. Now: ${describe(info)}`,
					);
				}
				const servers =
					params.action === "reconnect" ? await controller.reconnect(params.server) : controller.servers();
				return text(servers.length ? servers.map(describe).join("\n") : "No MCP servers are configured.");
			} catch (error) {
				return text(error instanceof Error ? error.message : String(error), true);
			}
		},
	};
}

/** Inline extension registering {@link createMcpConnectorsTool} for one engine session. */
export function mcpConnectorsExtension(getManager: () => MCPManager | undefined): ExtensionFactory {
	return api => {
		api.registerTool(createMcpConnectorsTool(getManager));
	};
}
