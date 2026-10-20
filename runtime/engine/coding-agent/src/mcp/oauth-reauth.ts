/**
 * MCP OAuth sign-in, independent of any UI: finding the server's config,
 * discovering its OAuth endpoints, running the browser authorization-code
 * flow, storing the credential and folding the result back into the config.
 *
 * The TUI's `/mcp reauth` and the RPC `mcp_login` command share it; each
 * supplies only its own presentation (where the authorization URL goes,
 * progress, cancellation).
 */
import * as path from "node:path";
import { getMCPConfigPath } from "@oh-my-pi/pi-utils";
import { expandEnvVarsDeep } from "../discovery/helpers";
import type { AuthStorage } from "../session/auth-storage";
import { readMCPConfigFile, updateMCPServer } from "./config-writer";
import type { MCPManager } from "./manager";
import {
	lookupMcpOAuthCredentialForServer,
	mcpOAuthCredentialIdsForServerUrl,
	removeManagedMcpOAuthCredential,
	removeManagedMcpOAuthCredentials,
} from "./oauth-credentials";
import { analyzeAuthError, discoverOAuthEndpoints, fetchResourceMetadataScopes, type OAuthEndpoints } from "./oauth-discovery";
import { MCPOAuthFlow, type MCPStoredOAuthCredential, mcpOAuthCredentialId } from "./oauth-flow";
import type { MCPAuthChallenge, MCPAuthConfig, MCPServerConfig } from "./types";

/** A server's config and the file a sign-in writes back to. */
export interface McpAuthTarget {
	filePath: string;
	scope: "user" | "project";
	config: MCPServerConfig;
	/** Found only at runtime (another tool's config, a plugin): any write goes to the user file. */
	discovered: boolean;
}

/** Everything one browser authorization needs. */
export interface McpOAuthFlowRequest {
	authorizationUrl: string;
	tokenUrl: string;
	clientId: string;
	clientSecret: string;
	scopes: string;
	callbackPort?: number;
	callbackPath?: string;
	redirectUri?: string;
	prompt?: string;
	serverUrl?: string;
	registrationUrl?: string;
	/** DCR `client_name` override (`oauth.clientName`). */
	clientName?: string;
	issuerUrl?: string;
	resource?: string;
	stripSameOriginResource?: boolean;
}

/**
 * Outcome of one authorization. `credentialId` is deterministic per server URL
 * when the URL was supplied, so every profile resolves its own credential row
 * under the same id. DCR-issued client secrets stay embedded in the stored
 * credential and are deliberately not surfaced, so they cannot leak into
 * config files.
 */
export interface McpOAuthFlowResult {
	credentialId: string;
	clientId?: string;
	resource?: string;
}

/** How the caller presents and bounds one authorization. */
export interface McpOAuthFlowControl {
	onAuth: (info: { url: string; launchUrl?: string; instructions?: string }) => void;
	onProgress?: (message: string) => void;
	onManualCodeInput?: (signal?: AbortSignal) => Promise<string>;
	/** Aborting tears the flow down; the rejection is `createAbortError()`. */
	signal: AbortSignal;
	createAbortError: () => Error;
	timeoutMs: number;
	/** Called when the deadline passes, before the rejection. */
	onTimeout: () => void;
}

export const MCP_OAUTH_TIMEOUT_MS = 5 * 60 * 1000;

export function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string, onTimeout?: () => void): Promise<T> {
	const { promise: timeoutPromise, reject } = Promise.withResolvers<T>();
	const timer = setTimeout(() => {
		onTimeout?.();
		reject(new Error(message));
	}, timeoutMs);
	return Promise.race([promise, timeoutPromise]).finally(() => clearTimeout(timer));
}

export function raceAbortSignal<T>(promise: Promise<T>, signal: AbortSignal, createError: () => Error): Promise<T> {
	if (signal.aborted) return Promise.reject(createError());

	const aborted = Promise.withResolvers<never>();
	const onAbort = (): void => aborted.reject(createError());
	signal.addEventListener("abort", onAbort, { once: true });
	return Promise.race([promise, aborted.promise]).finally(() => {
		signal.removeEventListener("abort", onAbort);
	});
}

export function stripMcpOAuthAuth(config: MCPServerConfig): MCPServerConfig {
	const next = { ...config } as MCPServerConfig & { auth?: MCPAuthConfig };
	delete next.auth;
	return next;
}

/** A server in omp's writable config files (user, project) or the project's standalone mcp.json/.mcp.json. */
export async function findConfiguredMcpServer(
	cwd: string,
	name: string,
): Promise<{ filePath: string; scope: "user" | "project"; config: MCPServerConfig } | null> {
	const userPath = getMCPConfigPath("user", cwd);
	const projectPath = getMCPConfigPath("project", cwd);

	const [userConfig, projectConfig] = await Promise.all([readMCPConfigFile(userPath), readMCPConfigFile(projectPath)]);

	if (userConfig.mcpServers?.[name]) {
		return { filePath: userPath, scope: "user", config: userConfig.mcpServers[name] };
	}
	if (projectConfig.mcpServers?.[name]) {
		return { filePath: projectPath, scope: "project", config: projectConfig.mcpServers[name] };
	}

	// Standalone fallback files (mcp.json, .mcp.json) in the project root match
	// the discovery paths used by the mcp-json provider. Reads run in parallel
	// but precedence is preserved by iteration order: mcp.json wins.
	const standalonePaths = [path.join(cwd, "mcp.json"), path.join(cwd, ".mcp.json")];
	const fallbackConfigs = await Promise.all(
		standalonePaths.map(async fallbackPath => {
			try {
				return await readMCPConfigFile(fallbackPath);
			} catch {
				// Malformed JSON in a standalone file — skip and continue lookup.
				return null;
			}
		}),
	);
	for (const [index, fallbackConfig] of fallbackConfigs.entries()) {
		const config = fallbackConfig?.mcpServers?.[name];
		if (config) {
			return { filePath: standalonePaths[index]!, scope: "project", config };
		}
	}
	return null;
}

/**
 * Resolve a server for an auth operation. Also recognizes runtime-discovered
 * servers that live in no writable config (a Claude Code plugin, `.cursor/mcp.json`):
 * any persisted change is written into the user config under the same name,
 * where the native provider shadows the discovered entry on the next reload.
 */
export async function resolveMcpServerForAuth(
	cwd: string,
	name: string,
	manager: MCPManager | undefined,
): Promise<McpAuthTarget | null> {
	const found = await findConfiguredMcpServer(cwd, name);
	if (found) return { ...found, discovered: false };

	const config = manager?.getServerConfig(name);
	const source = manager?.getSource(name);
	if (!config || !source) return null;

	return { filePath: getMCPConfigPath("user", cwd), scope: "user", config, discovered: true };
}

/**
 * Sign a server out: drop the credential its auth block points at and this
 * profile's url-keyed binding (runtime discovery expands `${...}` URL values
 * before looking up that deterministic row, so both spellings are cleared),
 * then strip the auth block from its config file. A runtime-discovered server
 * without an auth block of its own keeps its config untouched. Returns false
 * when there was nothing to remove. The TUI's `/mcp unauth` and RPC `mcp_logout` share it.
 */
export async function signOutMcpServer(name: string, target: McpAuthTarget, authStorage: AuthStorage): Promise<boolean> {
	const currentAuth = (target.config as MCPServerConfig & { auth?: MCPAuthConfig }).auth;
	if (currentAuth?.type === "oauth") await removeManagedMcpOAuthCredential(authStorage, currentAuth.credentialId);
	let removedUrlKeyedCredential = false;
	if ((target.config.type === "http" || target.config.type === "sse") && target.config.url) {
		removedUrlKeyedCredential = await removeManagedMcpOAuthCredentials(
			authStorage,
			mcpOAuthCredentialIdsForServerUrl(target.config.url),
		);
	}
	if (target.discovered && currentAuth?.type !== "oauth") return removedUrlKeyedCredential;
	await updateMCPServer(target.filePath, name, stripMcpOAuthAuth(target.config));
	return true;
}

/**
 * The OAuth endpoints of an http/sse server: probes it without credentials
 * (`testUnauthenticated` connects and throws the server's refusal), then
 * follows the challenge and RFC 8414/9728 discovery.
 */
export async function resolveMcpOAuthEndpoints(
	config: MCPServerConfig,
	testUnauthenticated: (config: MCPServerConfig) => Promise<void>,
	authChallenge?: MCPAuthChallenge,
): Promise<OAuthEndpoints> {
	// Stdio servers manage credentials inside the child process; OMP's OAuth
	// flow only applies to http/sse transports. Without this guard the
	// unauthenticated preflight below spawns the child, which happily reuses
	// its own cached tokens (e.g. mcp-remote's machine-wide ~/.mcp-auth) and
	// produces the misleading "reauthorization is not required".
	if (config.type !== "http" && config.type !== "sse") {
		const remoteUrl = config.args?.find(arg => /^https?:\/\//.test(arg));
		const httpHint = `{ "type": "http", "url": ${JSON.stringify(remoteUrl ?? "<remote url>")} }`;
		const usesMcpRemote = [config.command, ...(config.args ?? [])].some(part => part?.includes("mcp-remote"));
		throw new Error(
			usesMcpRemote
				? `this server proxies OAuth through mcp-remote, which caches tokens machine-wide in ~/.mcp-auth (shared across every OMP profile). Clear ~/.mcp-auth to force a fresh login, or replace the proxy with ${httpHint} so OMP manages OAuth per profile.`
				: `stdio servers manage their own credentials, so OMP has no OAuth to reauthorize. If the service supports OAuth over HTTP, configure it as ${httpHint} instead.`,
		);
	}
	// First test if server actually needs auth by connecting without OAuth
	let connectionSucceeded = false;
	let connectionError: Error | undefined;
	try {
		await testUnauthenticated(stripMcpOAuthAuth(config));
		connectionSucceeded = true;
	} catch (error) {
		connectionError = error as Error;
	}

	// Server connected fine without auth. A tool-level challenge overrides
	// this: servers may allow the anonymous handshake yet protect individual
	// tool calls with `_meta["mcp/www_authenticate"]`. Even without such a
	// challenge, a clean `initialize` is only weak evidence — per the MCP
	// spec a server MAY permit unauthenticated `initialize` while requiring a
	// bearer token for `tools/call`. The user explicitly asked to reauth, so
	// honor it when the server advertises OAuth discovery metadata; only
	// refuse when there is genuinely no OAuth endpoint to acquire.
	if (connectionSucceeded && !authChallenge) {
		const discovered = "url" in config && config.url ? await discoverOAuthEndpoints(config.url) : null;
		if (!discovered) {
			throw new Error("Server connection succeeded without OAuth; reauthorization is not required.");
		}
		return discovered;
	}

	// Tool calls can carry richer RFC 6750/RFC 9728 hints than the original
	// connection error. Feed those hints through the same analyzer so
	// resource_metadata and scope reach protected-resource discovery.
	const authError = authChallenge
		? new Error(`${connectionError?.message ?? "HTTP 401"}\n${authChallenge.wwwAuthenticate.join("\n")}`)
		: connectionError!;
	const authResult = analyzeAuthError(authError, "url" in config ? config.url : undefined);
	let oauth = authResult.authType === "oauth" ? (authResult.oauth ?? null) : null;

	if (!oauth && (config.type === "http" || config.type === "sse") && config.url) {
		oauth = await discoverOAuthEndpoints(config.url, authResult.authServerUrl, authResult.resourceMetadataUrl, {
			protectedScopes: authResult.scopes,
		});
	}
	if (oauth && !oauth.scopes && authResult.resourceMetadataUrl) {
		// JSON-error-body path skips `discoverOAuthEndpoints`; fetch the
		// advertised protected-resource metadata for the required scopes.
		const scopes = await fetchResourceMetadataScopes(authResult.resourceMetadataUrl);
		if (scopes) oauth = { ...oauth, scopes };
	}

	if (!oauth) {
		throw new Error("Could not discover OAuth endpoints from server response.");
	}

	return oauth;
}

/**
 * Run one browser authorization and store the credential. Embeds refresh
 * material so the credential is self-contained: token refresh must work for
 * configs that carry no auth block at all.
 */
export async function runMcpOAuthFlow(
	authStorage: AuthStorage,
	request: McpOAuthFlowRequest,
	control: McpOAuthFlowControl,
): Promise<McpOAuthFlowResult> {
	let parsedAuthUrl: URL;
	try {
		parsedAuthUrl = new URL(request.authorizationUrl);
		new URL(request.tokenUrl);
	} catch {
		throw new Error(
			`Invalid OAuth URLs. Please check:\n  Authorization URL: ${request.authorizationUrl}\n  Token URL: ${request.tokenUrl}`,
		);
	}
	const resolvedClientId =
		request.clientId.trim() || parsedAuthUrl.searchParams.get("client_id")?.trim() || undefined;
	const resolvedClientSecret = request.clientSecret.trim() || undefined;

	const flow = new MCPOAuthFlow(
		{
			authorizationUrl: request.authorizationUrl,
			tokenUrl: request.tokenUrl,
			registrationUrl: request.registrationUrl,
			clientName: request.clientName,
			issuerUrl: request.issuerUrl,
			clientId: resolvedClientId,
			clientSecret: resolvedClientSecret,
			scopes: request.scopes || undefined,
			prompt: request.prompt,
			redirectUri: request.redirectUri,
			callbackPort: request.callbackPort,
			callbackPath: request.callbackPath,
			resource: request.resource,
			stripSameOriginResource: request.stripSameOriginResource,
		},
		{
			onAuth: control.onAuth,
			onProgress: control.onProgress,
			onManualCodeInput: control.onManualCodeInput,
			signal: control.signal,
		},
	);
	if (control.signal.aborted) throw control.createAbortError();

	// Race the login itself against the abort signal: an abort may fire before
	// MCPOAuthFlow reaches its callback wait, where the callback server would
	// normally observe the signal.
	const credentials = await withTimeout(
		raceAbortSignal(flow.login(), control.signal, control.createAbortError),
		control.timeoutMs,
		"OAuth flow timed out after 5 minutes",
		control.onTimeout,
	);

	// Deterministic per-URL id: every profile resolves its own credential row
	// under the same key, so shared project configs stay profile-isolated.
	// Random fallback only for flows that never knew the server URL.
	const credentialId = request.serverUrl
		? mcpOAuthCredentialId(request.serverUrl)
		: `mcp_oauth_${Date.now()}_${Math.random().toString(36).slice(2, 11)}`;

	const oauthCredential: MCPStoredOAuthCredential = {
		type: "oauth",
		...credentials,
		tokenUrl: request.tokenUrl,
		clientId: flow.resolvedClientId?.trim() || resolvedClientId,
		clientSecret: flow.registeredClientSecret ?? resolvedClientSecret,
		resource: flow.resource,
		authorizationUrl: flow.authorizationUrl,
	};
	await authStorage.credentials.set(credentialId, oauthCredential);

	return { credentialId, clientId: flow.resolvedClientId, resource: flow.resource };
}

/**
 * Fold a completed OAuth flow back into a server config. Owns the persistence
 * policy in one place: the auth block records the credential pointer plus
 * refresh material, the oauth block echoes the client id for pre-auth reuse,
 * and only a user-supplied client secret is ever written — DCR-issued secrets
 * stay embedded in the stored credential so they cannot leak into (possibly
 * shared/committed) config files.
 */
export function persistMcpOAuthResult(
	config: MCPServerConfig,
	result: McpOAuthFlowResult,
	opts: {
		tokenUrl: string;
		resource?: string;
		stripSameOriginResource?: boolean;
		clientId?: string;
		persistOAuthClientId?: boolean;
		userClientSecret?: string;
	},
): MCPServerConfig {
	const clientId = result.clientId?.trim() || opts.clientId?.trim() || config.oauth?.clientId?.trim();
	const oauthClientId = opts.persistOAuthClientId === false ? undefined : clientId;
	const resource =
		result.resource ?? (opts.stripSameOriginResource ? undefined : opts.resource) ?? config.auth?.resource;
	return {
		...config,
		auth: {
			type: "oauth",
			credentialId: result.credentialId,
			tokenUrl: opts.tokenUrl,
			clientId,
			clientSecret: opts.userClientSecret,
			resource,
		},
		oauth: {
			...config.oauth,
			clientId: oauthClientId,
		},
	};
}

/**
 * Reauthorize a server: resolve its endpoints, pick the client credentials
 * (configured, previously persisted, previously registered, then discovery),
 * run `runFlow`, garbage-collect a superseded credential and persist the new
 * auth block when the config needs one. Returns the config to connect with.
 * `onStart` fires once the endpoints are known, before the browser step.
 */
export async function reauthorizeMcpServer(options: {
	name: string;
	target: McpAuthTarget;
	authStorage: AuthStorage;
	testUnauthenticated: (config: MCPServerConfig) => Promise<void>;
	runFlow: (request: McpOAuthFlowRequest) => Promise<McpOAuthFlowResult>;
	authChallenge?: MCPAuthChallenge;
	onStart?: () => void;
}): Promise<MCPServerConfig> {
	const { target, authStorage } = options;
	const currentAuth = (target.config as MCPServerConfig & { auth?: MCPAuthConfig }).auth;
	const baseConfig = stripMcpOAuthAuth(target.config);
	const runtimeBaseConfig = expandEnvVarsDeep(baseConfig);
	// Resolve endpoints first: this fails fast for stdio transports and
	// probes http/sse with { oauth: false }, so nothing destructive has
	// happened yet if the server turns out not to need (or support) OAuth.
	// Use the same env-expanded config shape runtime discovery passes to
	// MCPManager; the raw file value may contain `${...}` placeholders.
	const oauth = await resolveMcpOAuthEndpoints(runtimeBaseConfig, options.testUnauthenticated, options.authChallenge);
	const serverUrl =
		runtimeBaseConfig.type === "http" || runtimeBaseConfig.type === "sse" ? runtimeBaseConfig.url : undefined;
	// Client credentials drive the token exchange, so they must come from the
	// env-expanded runtime config; `target.config`/`currentAuth` may still hold
	// `${...}` placeholders (the wizard writes the secret to auth.clientSecret).
	// DCR secrets are embedded in the stored credential and never echoed back
	// into config files.
	const runtimeAuth = currentAuth ? expandEnvVarsDeep(currentAuth) : undefined;
	const configuredClientId = runtimeBaseConfig.oauth?.clientId?.trim() || undefined;
	const configuredClientSecret = runtimeBaseConfig.oauth?.clientSecret;
	const existingCredential = lookupMcpOAuthCredentialForServer(authStorage, currentAuth, serverUrl)?.credential;
	const persistedClientId = runtimeAuth?.clientId?.trim() || undefined;
	const storedClientId = existingCredential?.clientId?.trim() || undefined;
	const discoveredClientId = oauth.clientId?.trim() || undefined;
	// A metadata-advertised client is only a fallback. Prefer DCR when the
	// authorization server explicitly offers it, while retaining configured
	// and previously registered client credentials above.
	const flowClientId =
		configuredClientId ??
		persistedClientId ??
		storedClientId ??
		(oauth.registrationUrl ? undefined : discoveredClientId) ??
		"";
	const storedClientSecret = storedClientId === flowClientId ? existingCredential?.clientSecret : undefined;
	const flowClientSecret =
		(configuredClientId === flowClientId ? configuredClientSecret : undefined) ??
		(persistedClientId === flowClientId ? runtimeAuth?.clientSecret : undefined) ??
		storedClientSecret ??
		"";
	// Persisted separately below: keep the raw `${...}` placeholder in the file
	// rather than writing the resolved secret back to (possibly shared) config.
	const userClientSecret =
		(configuredClientId === flowClientId ? target.config.oauth?.clientSecret : undefined) ??
		(persistedClientId === flowClientId ? currentAuth?.clientSecret : undefined);
	const hasConfiguredOnlySecret = configuredClientId === undefined && configuredClientSecret !== undefined;

	options.onStart?.();

	const currentAuthResource = currentAuth?.resource ? expandEnvVarsDeep(currentAuth.resource) : undefined;
	const oauthResource =
		oauth.resource ?? currentAuthResource ?? ("url" in runtimeBaseConfig ? runtimeBaseConfig.url : undefined);
	const oauthResourceIsFallback = !oauth.resource && !currentAuthResource;

	const oauthResult = await options.runFlow({
		authorizationUrl: oauth.authorizationUrl,
		tokenUrl: oauth.tokenUrl,
		clientId: flowClientId,
		clientSecret: flowClientSecret,
		scopes: oauth.scopes || runtimeBaseConfig.oauth?.scope || "",
		callbackPort: target.config.oauth?.callbackPort,
		callbackPath: target.config.oauth?.callbackPath,
		redirectUri: target.config.oauth?.redirectUri,
		prompt: target.config.oauth?.prompt,
		registrationUrl: oauth.registrationUrl,
		clientName: runtimeBaseConfig.oauth?.clientName,
		issuerUrl: oauth.issuerUrl,
		serverUrl,
		resource: oauthResource,
		stripSameOriginResource: oauthResourceIsFallback,
	});

	// The flow overwrote (or minted) this profile's row; a superseded
	// pointer row from the legacy random-id era is now orphaned. GC only
	// after success so cancelling the browser step leaves the previous
	// session signed in.
	if (currentAuth?.type === "oauth" && currentAuth.credentialId !== oauthResult.credentialId) {
		await removeManagedMcpOAuthCredential(authStorage, currentAuth.credentialId);
	}

	// Definition-only entries resolve through the url-keyed binding alone;
	// skip the write-back so a committed project mcp.json stays clean.
	const urlKeyedId = serverUrl ? mcpOAuthCredentialId(serverUrl) : undefined;
	const shouldPersist = currentAuth || oauthResult.credentialId !== urlKeyedId;
	if (!shouldPersist) return baseConfig;
	const updatedConfig = persistMcpOAuthResult(baseConfig, oauthResult, {
		tokenUrl: oauth.tokenUrl,
		// Do not turn a configured-only secret into a persisted oauth
		// client pair by echoing the discovered client id.
		clientId: oauth.clientId,
		persistOAuthClientId: !hasConfiguredOnlySecret,
		userClientSecret,
		resource: oauthResource,
		stripSameOriginResource: oauthResourceIsFallback,
	});
	await updateMCPServer(target.filePath, options.name, updatedConfig);
	return updatedConfig;
}
