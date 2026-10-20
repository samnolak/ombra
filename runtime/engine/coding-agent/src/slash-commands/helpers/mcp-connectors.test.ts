import { describe, expect, test } from "bun:test";
import type { RpcMcpServerInfo } from "../../modes/rpc/rpc-types";
import type { SlashCommandRuntime, SlashMcpConnectors } from "../types";
import { handleMcpAcp } from "./mcp";

const info = (name: string, auth: RpcMcpServerInfo["auth"]): RpcMcpServerInfo => ({
	name,
	transport: "http",
	status: auth === "signed_in" ? "connected" : "failed",
	toolCount: auth === "signed_in" ? 3 : 0,
	auth,
});

/** A runtime whose host serves connectors (RPC) or not (ACP); records output, calls and background work. */
function runtime(connectors: boolean) {
	const output: string[] = [];
	const calls: string[] = [];
	const background: Promise<void>[] = [];
	const served: SlashMcpConnectors = {
		login: async name => {
			calls.push(`login ${name}`);
			if (name === "broken") throw new Error("Figma refused client registration (Forbidden)");
			return info(name, "signed_in");
		},
		logout: async name => {
			calls.push(`logout ${name}`);
			return info(name, "signed_out");
		},
		reconnect: async name => {
			calls.push(`reconnect ${name ?? "*"}`);
			return [info("docs", "signed_in"), info("figma-remote", "signed_out")];
		},
	};
	const value = {
		cwd: "/tmp",
		output: (text: string) => {
			output.push(text);
		},
		refreshCommands: () => {},
		runCommandInBackground: (task: () => Promise<void>) => {
			background.push(task());
		},
		...(connectors ? { mcpConnectors: served } : {}),
	} as unknown as SlashCommandRuntime; // Only the fields `/mcp` connector verbs read.
	const run = async (args: string) => {
		await handleMcpAcp({ name: "mcp", args, text: `/mcp ${args}` }, value);
		await Promise.all(background.splice(0));
	};
	return { output, calls, run };
}

describe("/mcp connector verbs in RPC hosts", () => {
	test("reauth and login run the host's sign-in in the background and report the result", async () => {
		const r = runtime(true);
		await r.run("reauth figma-remote");
		await r.run("login broken");
		expect(r.calls).toEqual(["login figma-remote", "login broken"]);
		expect(r.output).toContain('Signed in to "figma-remote": connected, sign-in signed_in, 3 tools');
		expect(r.output).toContain("/mcp login broken failed: Figma refused client registration (Forbidden)");
	});

	test("unauth signs out; reconnect and reload re-read the config", async () => {
		const r = runtime(true);
		await r.run("unauth docs");
		await r.run("reconnect figma-remote");
		await r.run("reload");
		expect(r.calls).toEqual(["logout docs", "reconnect figma-remote", "reconnect *"]);
		expect(r.output[0]).toBe('Signed out of "docs": failed, sign-in signed_out, 0 tools');
		expect(r.output[1]).toBe('Reloaded MCP config:\n"figma-remote": failed, sign-in signed_out, 0 tools');
		expect(r.output[2]?.split("\n")).toHaveLength(3);
	});

	test("a name is required to sign in or out", async () => {
		const r = runtime(true);
		await r.run("reauth");
		expect(r.calls).toEqual([]);
		expect(r.output).toEqual(["Usage: /mcp reauth <name>"]);
	});

	test("without host connectors (ACP) reauth stays TUI-only", async () => {
		const r = runtime(false);
		await r.run("reauth docs");
		expect(r.output[0]).toContain("only available in the TUI client");
	});
});
