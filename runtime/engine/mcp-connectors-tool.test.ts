import { describe, expect, test } from "bun:test";
import type { MCPManager } from "./coding-agent/src/mcp/manager";
import type { MCPServerConfig } from "./coding-agent/src/mcp/types";
import { RpcMcpController } from "./coding-agent/src/modes/rpc/rpc-mcp";
import type { AgentSession } from "./coding-agent/src/session/agent-session";
import { createMcpConnectorsTool } from "./mcp-connectors-tool";

/** The MCPManager surface RpcMcpController reads: one remote server refused for want of sign-in, one local server. */
function fakeManager() {
	const configs: Record<string, MCPServerConfig> = {
		"figma-remote": { type: "http", url: "https://mcp.figma.example/mcp" },
		local: { type: "stdio", command: "true" },
	};
	const value = {
		setAuthHandler() {},
		setElicitationHandler() {},
		addConnectionStatusListener: () => () => {},
		getAllServerNames: () => Object.keys(configs),
		getConnectionStatus: (name: string) => (name === "local" ? "connected" : "disconnected"),
		getServerFailure: (name: string) => (name === "local" ? undefined : "HTTP 401: Unauthorized"),
		getServerConfig: (name: string) => configs[name],
		getSource: () => ({ level: "user" }),
		getTools: () => [],
	};
	return value as unknown as MCPManager; // Only the methods the controller's status and prompt paths call.
}

const session = { modelRegistry: { authStorage: null } } as unknown as AgentSession; // Status reads only auth storage.

/** Calls the tool as the agent does; answers its text and error flag. */
function connectorsTool(getManager: () => MCPManager | undefined) {
	const tool = createMcpConnectorsTool(getManager);
	return async (params: { action: "status" | "login" | "reconnect"; server?: string }) => {
		const result = await tool.execute("call-1", params, undefined, undefined, undefined as never);
		const first = result.content[0];
		return { text: first && "text" in first ? first.text : "", isError: "isError" in result && result.isError === true };
	};
}

describe("mcp_connectors (agent)", () => {
	test("status lists the session's servers; login asks the host to offer sign-in, opening nothing", async () => {
		const manager = fakeManager();
		const frames: object[] = [];
		const controller = new RpcMcpController(session, manager, frame => frames.push(frame), async (_r, fallback) => fallback);
		const call = connectorsTool(() => manager);
		try {
			frames.length = 0; // The startup catch-up prompt for figma-remote.
			const status = await call({ action: "status" });
			expect(status.isError).toBe(false);
			expect(status.text).toBe(
				[
					"figma-remote [user] (http): failed, sign-in signed_out, 0 tools — HTTP 401: Unauthorized",
					"local [user] (stdio): connected, sign-in none, 0 tools",
				].join("\n"),
			);

			const login = await call({ action: "login", server: "figma-remote" });
			expect(login.text).toContain('Asked the user to sign in to "figma-remote"');
			expect(frames).toEqual([{ type: "mcp_auth_required", serverName: "figma-remote", reason: "signed_out" }]);

			expect(await call({ action: "login", server: "local" })).toMatchObject({ isError: true });
			expect(await call({ action: "login", server: "nope" })).toEqual({ text: 'MCP server "nope" not found', isError: true });
			expect(await call({ action: "login" })).toMatchObject({ isError: true });
			expect(frames).toHaveLength(1);
		} finally {
			controller.close();
		}
		expect(await call({ action: "status" })).toEqual({ text: "MCP connectors are not available in this session.", isError: true });
	});
});
