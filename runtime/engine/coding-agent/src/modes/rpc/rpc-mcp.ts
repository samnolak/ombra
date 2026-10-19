/**
 * MCP connectors over RPC: per-server state with sign-in status, OAuth sign-in
 * started by the host (`mcp_login`), re-auth prompts, reconnect, and form
 * elicitation as `extension_ui_request { method: "elicit" }`.
 *
 * Nothing here opens a browser: the authorization URL goes to the host as
 * `open_url` tagged with the server, only while the host's own `mcp_login`
 * runs. Credentials never leave the auth storage; frames carry names, states
 * and redacted errors only.
 */
import { Snowflake } from "@oh-my-pi/pi-utils";
import { connectToServer, disconnectServer } from "../../mcp/client";
import { loadAllMCPConfigs } from "../../mcp/config";
import type { MCPElicitationRequest, MCPElicitationResult } from "../../mcp/elicitation";
import type { MCPManager } from "../../mcp/manager";
import { hasMcpAuthorizationHeader, lookupMcpOAuthCredential } from "../../mcp/oauth-credentials";
import { analyzeAuthError } from "../../mcp/oauth-discovery";
import {
	MCP_OAUTH_TIMEOUT_MS,
	reauthorizeMcpServer,
	resolveMcpServerForAuth,
	runMcpOAuthFlow,
} from "../../mcp/oauth-reauth";
import { reloadSessionMcpServers } from "../../mcp/session-reload";
import { cfgMcpEnableProjectConfig } from "../../mcp/settings";
import type { AgentSession } from "../../session/agent-session";
import type { RpcExtensionUIResponse, RpcMcpAuthState, RpcMcpServerInfo } from "./rpc-types";

/** A refused command; `code` lets hosts tell a cancellation from a failure. */
export class RpcMcpError extends Error {
	constructor(
		message: string,
		readonly code?: string,
	) {
		super(message);
		this.name = "RpcMcpError";
	}
}

const LOOPBACK_HOSTS: Record<string, true> = { localhost: true, "127.0.0.1": true, "[::1]": true };
const MAX_ERROR_CHARS = 500;
const CANCELLED_CODE = "mcp_login_cancelled";
const TIMEOUT_REASON = "MCP OAuth flow timed out";

/**
 * Whether a URL may be handed to the user's browser: https, or plain http only
 * to this computer (a local server, omp's own callback). No credentials in the URL.
 */
export function isSafeAuthorizationUrl(raw: string): boolean {
	let url: URL;
	try {
		url = new URL(raw);
	} catch {
		return false;
	}
	if (url.username || url.password) return false;
	return url.protocol === "https:" || (url.protocol === "http:" && LOOPBACK_HOSTS[url.hostname] === true);
}

/** A connection error fit for the host: URL queries and fragments (keys ride there) removed, length capped. */
export function redactMcpError(message: string): string {
	const redacted = message.replace(/(\b[a-z][a-z0-9+.-]*:\/\/[^\s?#"'<>\]]*)[?#][^\s"'<>\]]*/gi, "$1");
	return redacted.length > MAX_ERROR_CHARS ? `${redacted.slice(0, MAX_ERROR_CHARS)}…` : redacted;
}

/** Shows `request` as a host dialog; resolves `fallback` when it is withdrawn. */
export type RpcMcpDialog = <T>(
	request: Record<string, unknown>,
	fallback: T,
	parse: (response: RpcExtensionUIResponse) => T,
) => Promise<T>;

export class RpcMcpController {
	readonly #session: AgentSession;
	readonly #manager: MCPManager | undefined;
	readonly #output: (frame: object) => void;
	readonly #dialog: RpcMcpDialog;
	readonly #logins = new Map<string, AbortController>();
	/** Servers whose tool calls hit an OAuth challenge since they last connected. */
	readonly #challenged = new Set<string>();
	/** Servers that had a working credential in this session: a later refusal means it expired. */
	readonly #signedIn = new Set<string>();
	/** The prompt last sent per server, so one episode prompts once. */
	readonly #prompted = new Map<string, RpcMcpAuthState>();
	readonly #unsubscribe: (() => void) | undefined;

	constructor(session: AgentSession, manager: MCPManager | undefined, output: (frame: object) => void, dialog: RpcMcpDialog) {
		this.#session = session;
		this.#manager = manager;
		this.#output = output;
		this.#dialog = dialog;
		if (!manager) return;
		// A tool-level OAuth challenge: tell the host, never start a browser flow on our own.
		manager.setAuthHandler(async serverName => {
			this.#challenged.add(serverName);
			this.#promptIfNeeded(serverName);
			return undefined;
		});
		manager.setElicitationHandler((serverName, request) => this.#elicit(serverName, request));
		this.#unsubscribe = manager.addConnectionStatusListener(event => {
			if (event.type === "connected") this.#onConnected(event.serverName);
			else if (event.type === "failed") this.#promptIfNeeded(event.serverName);
		});
		// Discovery starts with the session, before this runtime exists: catch up on what already happened.
		for (const name of manager.getAllServerNames()) {
			if (manager.getConnectionStatus(name) === "connected") this.#onConnected(name);
			else this.#promptIfNeeded(name);
		}
	}

	servers(): RpcMcpServerInfo[] {
		const manager = this.#manager;
		return manager ? manager.getAllServerNames().map(name => this.#info(manager, name)) : [];
	}

	/** Sign in to one server: the URL goes to the host as `open_url`; resolves once connected again. */
	async login(serverName: string): Promise<RpcMcpServerInfo> {
		const manager = this.#require();
		const target = await resolveMcpServerForAuth(this.#session.sessionManager.getCwd(), serverName, manager);
		if (!target) throw new RpcMcpError(`MCP server "${serverName}" not found`);
		if (target.config.enabled === false) throw new RpcMcpError(`MCP server "${serverName}" is disabled`);

		// A second sign-in for the same server replaces the first.
		this.#logins.get(serverName)?.abort("superseded");
		const abort = new AbortController();
		this.#logins.set(serverName, abort);
		const cancelled = () => new RpcMcpError("MCP sign-in cancelled", CANCELLED_CODE);
		const authStorage = this.#session.modelRegistry.authStorage;
		try {
			await reauthorizeMcpServer({
				name: serverName,
				target,
				authStorage,
				testUnauthenticated: async config => {
					const connection = await connectToServer(serverName, await manager.prepareConfig(config, { oauth: false }));
					await disconnectServer(connection);
				},
				runFlow: request => {
					if (!isSafeAuthorizationUrl(request.authorizationUrl))
						throw new RpcMcpError(
							"The server's sign-in page is not served over https (or from this computer); refusing to open it.",
						);
					return runMcpOAuthFlow(authStorage, request, {
						onAuth: info => {
							if (!isSafeAuthorizationUrl(info.url)) {
								abort.abort("unsafe authorization URL");
								return;
							}
							// omp's own short redirect to `url`: plain http to this computer only.
							const launchUrl =
								info.launchUrl?.startsWith("http://") && isSafeAuthorizationUrl(info.launchUrl)
									? info.launchUrl
									: undefined;
							this.#output({
								type: "extension_ui_request",
								id: Snowflake.next() as string,
								method: "open_url",
								url: info.url,
								...(launchUrl ? { launchUrl } : {}),
								...(info.instructions ? { instructions: info.instructions } : {}),
								mcpServer: serverName,
							});
						},
						signal: abort.signal,
						createAbortError: cancelled,
						timeoutMs: MCP_OAUTH_TIMEOUT_MS,
						onTimeout: () => abort.abort(TIMEOUT_REASON),
					});
				},
			});
		} catch (error) {
			if (abort.signal.aborted && abort.signal.reason !== TIMEOUT_REASON) {
				throw abort.signal.reason === "unsafe authorization URL"
					? new RpcMcpError("The sign-in URL is not https (or this computer); refusing to open it.")
					: cancelled();
			}
			throw error;
		} finally {
			if (this.#logins.get(serverName) === abort) this.#logins.delete(serverName);
		}
		this.#challenged.delete(serverName);
		this.#prompted.delete(serverName);
		this.#signedIn.add(serverName);
		await this.#connect(manager, serverName);
		return this.#info(manager, serverName);
	}

	cancelLogin(serverName: string): boolean {
		const login = this.#logins.get(serverName);
		if (!login) return false;
		login.abort("cancelled");
		return true;
	}

	/** One server (from its current config) or, without a name, every server rediscovered. */
	async reconnect(serverName?: string): Promise<RpcMcpServerInfo[]> {
		const manager = this.#require();
		if (serverName === undefined) {
			this.#challenged.clear();
			this.#prompted.clear();
			await reloadSessionMcpServers(manager, this.#session, this.#session.settings);
			await manager.waitForPendingConnections();
			await this.#session.refreshMCPTools(manager.getTools());
		} else {
			if (!manager.getAllServerNames().includes(serverName))
				throw new RpcMcpError(`MCP server "${serverName}" not found`);
			this.#challenged.delete(serverName);
			this.#prompted.delete(serverName);
			await this.#connect(manager, serverName);
		}
		return this.servers();
	}

	close(): void {
		for (const login of this.#logins.values()) login.abort("RPC session closed");
		this.#logins.clear();
		this.#unsubscribe?.();
		this.#manager?.setAuthHandler(undefined);
		this.#manager?.setElicitationHandler(undefined);
	}

	#require(): MCPManager {
		if (!this.#manager) throw new RpcMcpError("MCP is off for this session");
		return this.#manager;
	}

	/** Connect one server afresh from the config files (a sign-in may have rewritten its auth block). */
	async #connect(manager: MCPManager, name: string): Promise<void> {
		const previous = manager.getServerConfig(name);
		const previousSource = manager.getSource(name);
		await manager.disconnectServer(name);
		const { configs, sources } = await loadAllMCPConfigs(this.#session.sessionManager.getCwd(), {
			enableProjectConfig: cfgMcpEnableProjectConfig.get(this.#session.settings),
			filterExa: true,
			extensionRoots: this.#session.effectiveExtensionRoots,
		});
		const config = configs[name] ?? previous;
		if (!config) return;
		const source = sources[name] ?? previousSource;
		// Window 0: wait for this server's connection and tools before answering.
		await manager.connectServers({ [name]: config }, source ? { [name]: source } : {}, undefined, 0);
		await this.#session.refreshMCPTools(manager.getTools());
	}

	#info(manager: MCPManager, name: string): RpcMcpServerInfo {
		const config = manager.getServerConfig(name);
		const connection = manager.getConnectionStatus(name);
		const failure = connection === "disconnected" ? manager.getServerFailure(name) : undefined;
		const level = manager.getSource(name)?.level;
		return {
			name,
			transport: config?.type ?? "stdio",
			...(level ? { scope: level } : {}),
			status: failure !== undefined ? "failed" : connection,
			toolCount: manager.getTools().filter(tool => tool.mcpServerName === name).length,
			...(failure !== undefined ? { error: redactMcpError(failure) } : {}),
			auth: this.#auth(manager, name),
		};
	}

	#auth(manager: MCPManager, name: string): RpcMcpAuthState {
		const config = manager.getServerConfig(name);
		if (!config) return "none";
		const lookup = lookupMcpOAuthCredential(this.#session.modelRegistry.authStorage, config);
		if (manager.getConnectionStatus(name) === "connected" && !this.#challenged.has(name))
			return lookup ? "signed_in" : "none";
		// A refused remote server is an OAuth sign-in matter unless it is configured with its own
		// Authorization header (an API key the user edits in the config, not a sign-in).
		const failure = manager.getServerFailure(name);
		const remote = config.type === "http" || config.type === "sse";
		const refused =
			remote &&
			failure !== undefined &&
			!hasMcpAuthorizationHeader(config) &&
			analyzeAuthError(new Error(failure), config.url).requiresAuth;
		if (this.#challenged.has(name) || refused) return lookup || this.#signedIn.has(name) ? "expired" : "signed_out";
		if (lookup) {
			const { expires, refresh } = lookup.credential;
			return expires !== undefined && expires <= Date.now() && !refresh ? "expired" : "signed_in";
		}
		return config.auth?.type === "oauth" ? "signed_out" : "none";
	}

	#onConnected(name: string): void {
		const manager = this.#manager;
		if (!manager) return;
		this.#challenged.delete(name);
		this.#prompted.delete(name);
		if (this.#auth(manager, name) === "signed_in") this.#signedIn.add(name);
	}

	#promptIfNeeded(name: string): void {
		const manager = this.#manager;
		if (!manager || this.#logins.has(name)) return;
		const state = this.#auth(manager, name);
		if (state !== "signed_out" && state !== "expired") return;
		if (this.#prompted.get(name) === state) return;
		this.#prompted.set(name, state);
		this.#output({ type: "mcp_auth_required", serverName: name, reason: state });
	}

	#elicit(serverName: string, request: MCPElicitationRequest): Promise<MCPElicitationResult> {
		const cancel: MCPElicitationResult = { action: "cancel" };
		return this.#dialog(
			{ method: "elicit", serverName, message: request.message, fields: request.fields },
			cancel,
			(response): MCPElicitationResult => {
				if (!("action" in response)) return cancel;
				return response.action === "accept"
					? { action: "accept", content: response.content ?? {} }
					: { action: "decline" };
			},
		).catch(() => cancel);
	}
}
