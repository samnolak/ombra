// On-demand MCP servers: the agent enables a server when the task needs it and disables it afterwards.
// Servers listed in servers.json (next to this file) are not in mcp.json, so omp never connects them at startup.
// Dependency-free MCP client: stdio (newline-delimited JSON-RPC) and Streamable HTTP (JSON or SSE responses).
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { readFileSync } from "node:fs";
import { join } from "node:path";

type ServerConfig =
	| { description: string; type?: "stdio"; command: string; args?: string[]; env?: Record<string, string>; cwd?: string; timeout?: number }
	| { description: string; type: "http"; url: string; headers?: Record<string, string>; timeout?: number };

type JsonRpcResponse = { id?: number; result?: unknown; error?: { code: number; message: string } };
type McpTool = { name: string; description?: string; inputSchema?: unknown };
type McpContent = { type: string; text?: string; data?: string; mimeType?: string };
type ToolContent = { type: "text"; text: string } | { type: "image"; data: string; mimeType: string };

interface Connection {
	request(method: string, params: unknown, signal?: AbortSignal): Promise<unknown>;
	close(): Promise<void>;
	instructions?: string;
	toolCount: number;
}

const CONFIG_PATH = join(import.meta.dir, "servers.json");
const DEFAULT_TIMEOUT = 60_000;
const PROTOCOL_VERSION = "2025-06-18";

function loadServers(): Record<string, ServerConfig> {
	return JSON.parse(readFileSync(CONFIG_PATH, "utf8"));
}

function text(t: string, isError = false) {
	return { content: [{ type: "text" as const, text: t }], isError, details: {} };
}

function unwrap(msg: JsonRpcResponse): unknown {
	if (msg.error) throw new Error(`MCP error ${msg.error.code}: ${msg.error.message}`);
	return msg.result;
}

function withTimeout<T>(promise: Promise<T>, ms: number, what: string): Promise<T> {
	let timer: Timer | undefined;
	const timeout = new Promise<never>((_, reject) => {
		timer = setTimeout(() => reject(new Error(`${what} timed out after ${ms} ms`)), ms);
	});
	return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

function stdioTransport(cfg: Extract<ServerConfig, { command: string }>, onClose: () => void) {
	const proc = Bun.spawn([cfg.command, ...(cfg.args ?? [])], {
		cwd: cfg.cwd,
		env: { ...process.env, ...cfg.env },
		stdin: "pipe",
		stdout: "pipe",
		stderr: "ignore",
	});
	const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
	let nextId = 1;
	let closed = false;

	(async () => {
		const decoder = new TextDecoder();
		let buffer = "";
		for await (const chunk of proc.stdout) {
			buffer += decoder.decode(chunk, { stream: true });
			let nl: number;
			while ((nl = buffer.indexOf("\n")) >= 0) {
				const line = buffer.slice(0, nl).trim();
				buffer = buffer.slice(nl + 1);
				if (!line) continue;
				let msg: JsonRpcResponse;
				try {
					msg = JSON.parse(line);
				} catch {
					continue; // servers sometimes log to stdout; not JSON-RPC
				}
				const waiter = msg.id === undefined ? undefined : pending.get(msg.id);
				if (!waiter) continue;
				pending.delete(msg.id!);
				try {
					waiter.resolve(unwrap(msg));
				} catch (err) {
					waiter.reject(err as Error);
				}
			}
		}
	})()
		.catch(() => {})
		.finally(() => {
			closed = true;
			for (const w of pending.values()) w.reject(new Error("MCP server process exited"));
			pending.clear();
			onClose();
		});

	const send = (msg: object) => {
		proc.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", ...msg })}\n`);
		proc.stdin.flush();
	};

	return {
		request(method: string, params: unknown, signal?: AbortSignal) {
			if (closed) return Promise.reject(new Error("MCP server process exited"));
			const id = nextId++;
			return new Promise<unknown>((resolve, reject) => {
				pending.set(id, { resolve, reject });
				signal?.addEventListener("abort", () => {
					pending.delete(id);
					send({ method: "notifications/cancelled", params: { requestId: id } });
					reject(new Error("aborted"));
				});
				send({ id, method, params });
			});
		},
		notify(method: string) {
			send({ method });
		},
		async close() {
			proc.kill();
			await proc.exited;
		},
	};
}

function httpTransport(cfg: Extract<ServerConfig, { url: string }>) {
	let sessionId: string | undefined;
	let nextId = 1;

	const post = async (body: object, signal?: AbortSignal) => {
		const headers: Record<string, string> = {
			"content-type": "application/json",
			accept: "application/json, text/event-stream",
			"mcp-protocol-version": PROTOCOL_VERSION,
			...cfg.headers,
		};
		if (sessionId) headers["mcp-session-id"] = sessionId;
		const res = await fetch(cfg.url, { method: "POST", headers, body: JSON.stringify({ jsonrpc: "2.0", ...body }), signal });
		sessionId = res.headers.get("mcp-session-id") ?? sessionId;
		if (!res.ok) throw new Error(`HTTP ${res.status}: ${(await res.text()).slice(0, 300)}`);
		return res;
	};

	return {
		async request(method: string, params: unknown, signal?: AbortSignal) {
			const id = nextId++;
			const res = await post({ id, method, params }, signal);
			if (!(res.headers.get("content-type") ?? "").includes("text/event-stream")) return unwrap(await res.json());
			for (const event of (await res.text()).split(/\r?\n\r?\n/)) {
				const data = event
					.split(/\r?\n/)
					.filter(l => l.startsWith("data:"))
					.map(l => l.slice(5).trimStart())
					.join("\n");
				if (!data) continue;
				const msg: JsonRpcResponse = JSON.parse(data);
				if (msg.id === id) return unwrap(msg);
			}
			throw new Error(`no response to ${method} in the event stream`);
		},
		notify(method: string) {
			post({ method }).catch(() => {});
		},
		async close() {
			if (!sessionId) return;
			await fetch(cfg.url, { method: "DELETE", headers: { "mcp-session-id": sessionId, ...cfg.headers } }).catch(() => {});
		},
	};
}

export default function (pi: ExtensionAPI) {
	const z = pi.zod;
	const connections = new Map<string, Connection>();
	const catalog = Object.entries(loadServers())
		.map(([name, s]) => `- ${name}: ${s.description}`)
		.join("\n");

	async function connect(name: string): Promise<Connection> {
		const existing = connections.get(name);
		if (existing) return existing;
		const servers = loadServers();
		const cfg = servers[name];
		if (!cfg) throw new Error(`unknown server "${name}"; known: ${Object.keys(servers).join(", ")}`);
		const timeout = cfg.timeout ?? DEFAULT_TIMEOUT;
		const transport = cfg.type === "http" ? httpTransport(cfg) : stdioTransport(cfg, () => connections.delete(name));
		try {
			const init = (await withTimeout(
				transport.request("initialize", {
					protocolVersion: PROTOCOL_VERSION,
					capabilities: {},
					clientInfo: { name: "omp-lazy-mcp", version: "1.0.0" },
				}),
				timeout,
				`${name} initialize`,
			)) as { instructions?: string };
			transport.notify("notifications/initialized");
			const conn: Connection = {
				request: (method, params, signal) => withTimeout(transport.request(method, params, signal), timeout, `${name} ${method}`),
				close: () => transport.close(),
				instructions: init.instructions,
				toolCount: 0,
			};
			connections.set(name, conn);
			return conn;
		} catch (err) {
			await transport.close();
			throw err;
		}
	}

	async function disconnect(name: string) {
		const conn = connections.get(name);
		connections.delete(name);
		await conn?.close();
	}

	pi.registerTool({
		name: "mcp_lazy",
		label: "On-demand MCP",
		description: [
			"Enable, use and disable MCP servers on demand. Servers are OFF until you enable them.",
			"Workflow: action=enable (connects; returns the server's instructions and tools with input schemas) → action=call with tool + args → action=disable once the task no longer needs the server.",
			"Available servers:",
			catalog,
		].join("\n"),
		parameters: z.object({
			action: z.enum(["status", "enable", "disable", "call"]),
			server: z.string().optional().describe("Server name (required except for status)"),
			tool: z.string().optional().describe("Tool name for action=call"),
			args: z.record(z.string(), z.unknown()).optional().describe("Tool arguments for action=call"),
		}),
		async execute(_id, params, signal) {
			const { action, server, tool, args } = params;
			try {
				if (action === "status") {
					return text(
						Object.keys(loadServers())
							.map(n => `${n}: ${connections.has(n) ? `enabled (${connections.get(n)!.toolCount} tools)` : "disabled"}`)
							.join("\n"),
					);
				}
				if (!server) return text("`server` is required", true);
				if (action === "disable") {
					const was = connections.has(server);
					await disconnect(server);
					return text(was ? `${server} disabled` : `${server} was not enabled`);
				}
				const conn = await connect(server);
				if (action === "enable") {
					const { tools } = (await conn.request("tools/list", {}, signal)) as { tools: McpTool[] };
					conn.toolCount = tools.length;
					const body = tools
						.map(t => `## ${t.name}\n${t.description ?? ""}\ninputSchema: ${JSON.stringify(t.inputSchema)}`)
						.join("\n\n");
					const instructions = conn.instructions ? `\n\n# Server instructions\n${conn.instructions}` : "";
					return text(`${server} enabled.${instructions}\n\n# Tools\n${body}`);
				}
				if (!tool) return text("`tool` is required for action=call", true);
				const result = (await conn.request("tools/call", { name: tool, arguments: args ?? {} }, signal)) as {
					content?: McpContent[];
					structuredContent?: unknown;
					isError?: boolean;
				};
				const content: ToolContent[] = (result.content ?? []).map(c =>
					c.type === "text" && c.text !== undefined
						? { type: "text", text: c.text }
						: c.type === "image" && c.data && c.mimeType
							? { type: "image", data: c.data, mimeType: c.mimeType }
							: { type: "text", text: JSON.stringify(c) },
				);
				if (content.length === 0 && result.structuredContent !== undefined)
					content.push({ type: "text", text: JSON.stringify(result.structuredContent) });
				return { content, isError: Boolean(result.isError), details: {} };
			} catch (err) {
				return text(`${action} ${server ?? ""} failed: ${err instanceof Error ? err.message : String(err)}`, true);
			}
		},
	});

	pi.on("session_shutdown", async () => {
		await Promise.allSettled([...connections.keys()].map(disconnect));
	});
}
