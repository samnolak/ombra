import { afterEach, describe, expect, test } from "bun:test";
import type { FetchImpl } from "@oh-my-pi/pi-ai/types";
import { connectToServer, FIGMA_DESKTOP_NOT_RUNNING } from "./client";
import { getMcpOAuthClientName, MCPOAuthFlow, setMcpOAuthClientName } from "./oauth-flow";

const REFUSAL =
	"refused client registration (Forbidden): this client is not on the server allowlist; set oauth.clientName or a pre-registered clientId";

/** A provider whose DCR endpoint answers `register`; records every registered client name and authorize probe. */
function provider(origin: string, register: (clientName: string) => Response) {
	const seen = { names: [] as string[], authorizeProbes: 0 };
	const fetchImpl: FetchImpl = async (input, init) => {
		const url = String(input);
		if (url === `${origin}/register`) {
			const body = JSON.parse(String(init?.body)) as { client_name: string };
			seen.names.push(body.client_name);
			return register(body.client_name);
		}
		if (url.startsWith(`${origin}/authorize?`)) {
			seen.authorizeProbes++;
			return new Response("Missing required parameter: client_id", { status: 400 });
		}
		throw new Error(`Unexpected fetch: ${url}`);
	};
	const flow = (config: { clientName?: string; clientId?: string } = {}) =>
		new MCPOAuthFlow(
			{
				authorizationUrl: `${origin}/authorize`,
				tokenUrl: `${origin}/token`,
				registrationUrl: `${origin}/register`,
				fetch: fetchImpl,
				...config,
			},
			{},
		);
	return { seen, flow };
}

const registered = () => Response.json({ client_id: "issued-client" }, { status: 201 });

describe("MCP dynamic client registration name", () => {
	const original = getMcpOAuthClientName();
	afterEach(() => setMcpOAuthClientName(original));

	test("defaults to omp's name, follows the host's default and yields to oauth.clientName", async () => {
		const { seen, flow } = provider("https://auth.example", registered);
		await flow().generateAuthUrl("s", "http://127.0.0.1:53201/callback");
		setMcpOAuthClientName("Ombra");
		await flow().generateAuthUrl("s", "http://127.0.0.1:53201/callback");
		await flow({ clientName: "  Claude Code " }).generateAuthUrl("s", "http://127.0.0.1:53201/callback");
		setMcpOAuthClientName("   ");
		expect(getMcpOAuthClientName()).toBe("Ombra");
		expect(seen.names).toEqual(["oh-my-pi", "Ombra", "Claude Code"]);
	});

	test("a pre-registered client id skips registration", async () => {
		const { seen, flow } = provider("https://auth.example", registered);
		const { url } = await flow({ clientId: "pre-registered", clientName: "Ombra" }).generateAuthUrl(
			"s",
			"http://127.0.0.1:53202/callback",
		);
		expect(new URL(url).searchParams.get("client_id")).toBe("pre-registered");
		expect(seen.names).toEqual([]);
	});
});

describe("refused client registration", () => {
	test("Figma's Forbidden is final: the allowlist message, no client-less authorize page", async () => {
		const { seen, flow } = provider("https://api.figma.com/v1/oauth/mcp", () => new Response("Forbidden", { status: 403 }));
		const attempt = flow({ clientName: "Ombra" }).generateAuthUrl("s", "http://127.0.0.1:53203/callback");
		await expect(attempt).rejects.toThrow(`Figma ${REFUSAL}`);
		await expect(attempt).rejects.toThrow(/client_name "Ombra" → HTTP 403 — Forbidden.*oauth\.clientId/s);
		expect(seen.authorizeProbes).toBe(0);
	});

	test("elsewhere an explicit unapproved_client names the host and the allowlist", async () => {
		const { flow } = provider("https://auth.example", () =>
			Response.json({ error: "unapproved_client" }, { status: 403 }),
		);
		await expect(flow().generateAuthUrl("s", "http://127.0.0.1:53204/callback")).rejects.toThrow(
			`auth.example ${REFUSAL}`,
		);
	});

	test("elsewhere a bare 401 still probes the authorize page; one that needs a client id names the allowlist", async () => {
		const { seen, flow } = provider("https://auth.example", () => new Response("nope", { status: 401 }));
		await expect(flow().generateAuthUrl("s", "http://127.0.0.1:53205/callback")).rejects.toThrow(
			"auth.example refused client registration (Unauthorized): this client is not on the server allowlist",
		);
		expect(seen.authorizeProbes).toBe(1);
	});
});

describe("Figma desktop MCP server", () => {
	test("a refused connection on 127.0.0.1:3845 says how to enable it", async () => {
		const probe = await fetch("http://127.0.0.1:3845/mcp").then(
			() => true,
			() => false,
		);
		if (probe) return; // Figma Desktop is running its server on this machine: nothing to refuse.
		const attempt = connectToServer("figma", { type: "http", url: "http://127.0.0.1:3845/mcp", timeout: 10_000 });
		await expect(attempt).rejects.toThrow(FIGMA_DESKTOP_NOT_RUNNING);
	});

	test("other refused servers keep their own error", async () => {
		const attempt = connectToServer("local", { type: "http", url: "http://127.0.0.1:9/mcp", timeout: 10_000 });
		await expect(attempt).rejects.not.toThrow(FIGMA_DESKTOP_NOT_RUNNING);
	});
});
