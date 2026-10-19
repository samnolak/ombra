import { afterAll, afterEach, expect, test } from "bun:test";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";

// The real engine host (host.ts --mode engine) in an isolated profile: MCP OAuth sign-in, re-auth on expiry,
// reconnect and form elicitation over the session RPC, against local MCP servers made for the test.
const engineRoot = process.env.OMBRA_ENGINE_ROOT;
if (!engineRoot) throw new Error("Set OMBRA_ENGINE_ROOT to runtime/engine with the pinned omp/node_modules linked");
if (Bun.version !== "1.4.2") throw new Error(`Use the pack's pinned Bun 1.4.2, not ${Bun.version}`);

type Frame = Record<string, any>;
const runRoot = resolve(import.meta.dir, "../../.temp/ombra-tests", `run-${process.pid}`, "mcp-oauth");
const cleanups: Array<() => Promise<void> | void> = [];
afterEach(async () => { for (const cleanup of cleanups.splice(0).reverse()) await cleanup(); });
afterAll(() => rm(runRoot, { recursive: true, force: true }));

class Host {
	readonly frames: Frame[] = [];
	readonly exited: Promise<number | null>;
	readonly #process;
	#stderr = "";
	#sequence = 0;
	#changed = new Set<() => void>();
	constructor(env: NodeJS.ProcessEnv, cwd: string) {
		this.#process = spawn(process.execPath, [join(resolve(engineRoot!), "host.ts"), "--mode", "engine"], { cwd, env, stdio: ["pipe", "pipe", "pipe"] });
		const exited = Promise.withResolvers<number | null>();
		this.#process.once("exit", exited.resolve);
		this.exited = exited.promise;
		this.#process.stderr.on("data", data => { this.#stderr = (this.#stderr + data).slice(-16384); });
		createInterface({ input: this.#process.stdout }).on("line", line => {
			this.frames.push(JSON.parse(line));
			for (const wake of this.#changed) wake();
		});
	}
	get stderr(): string { return this.#stderr; }
	/** Frames of one logical session, unwrapped. */
	session(key: string): Frame[] {
		return this.frames.filter(frame => frame.type === "engine_session_frame" && frame.sessionKey === key).map(frame => frame.frame);
	}
	wait(predicate: (frame: Frame) => boolean, what: string, timeoutMs = 30_000): Promise<Frame> {
		const pending = Promise.withResolvers<Frame>();
		const finish = () => {
			const frame = this.frames.find(predicate);
			if (frame) { clearTimeout(timer); this.#changed.delete(finish); pending.resolve(frame); }
		};
		const timer = setTimeout(() => { this.#changed.delete(finish); pending.reject(new Error(`Timed out waiting for ${what}: ${this.#stderr}`)); }, timeoutMs);
		this.#changed.add(finish);
		finish();
		return pending.promise;
	}
	waitSession(key: string, predicate: (frame: Frame) => boolean, what: string, timeoutMs?: number): Promise<Frame> {
		return this.wait(frame => frame.type === "engine_session_frame" && frame.sessionKey === key && predicate(frame.frame), what, timeoutMs)
			.then(frame => frame.frame);
	}
	write(frame: object): void { this.#process.stdin.write(`${JSON.stringify(frame)}\n`); }
	async request(type: string, fields: object = {}): Promise<Frame> {
		const id = `host-${++this.#sequence}`;
		this.write({ ...fields, type, id });
		return this.wait(frame => frame.type === "response" && frame.id === id, type, 60_000);
	}
	/** Sends a session command; resolves with its response. */
	command(key: string, type: string, fields: object = {}, timeoutMs?: number): Promise<Frame> {
		const id = `cmd-${++this.#sequence}`;
		this.sendSession(key, { ...fields, type, id });
		return this.waitSession(key, frame => frame.type === "response" && frame.id === id, type, timeoutMs);
	}
	sendSession(key: string, frame: object): void { this.write({ type: "engine_session_command", sessionKey: key, frame }); }
	async stop(): Promise<void> {
		if (this.#process.exitCode !== null || this.#process.signalCode !== null) return;
		this.#process.kill("SIGKILL");
		await this.exited;
	}
}

/** A Streamable-HTTP MCP server behind OAuth 2.1 (RFC 9728 metadata, DCR, PKCE), like a hosted connector. */
function oauthServer(options: { authorizeEndpoint?: (origin: string) => string } = {}) {
	const state = { token: `access-${crypto.randomUUID()}`, refresh: `refresh-${crypto.randomUUID()}`, codes: new Set<string>(),
		refreshWorks: true, issued: 0 };
	const secrets = () => [state.token, state.refresh, ...state.codes];
	const server = Bun.serve({
		hostname: "127.0.0.1",
		port: 0,
		async fetch(request) {
			const url = new URL(request.url);
			const origin = `http://127.0.0.1:${server.port}`;
			if (url.pathname.startsWith("/.well-known/oauth-protected-resource"))
				return Response.json({ resource: `${origin}/mcp`, authorization_servers: [origin] });
			if (url.pathname === "/.well-known/oauth-authorization-server")
				return Response.json({
					issuer: origin,
					authorization_endpoint: options.authorizeEndpoint?.(origin) ?? `${origin}/authorize`,
					token_endpoint: `${origin}/token`,
					registration_endpoint: `${origin}/register`,
					response_types_supported: ["code"],
					grant_types_supported: ["authorization_code", "refresh_token"],
					code_challenge_methods_supported: ["S256"],
					token_endpoint_auth_methods_supported: ["none"],
				});
			if (url.pathname === "/register") {
				const body = await request.json() as { redirect_uris?: string[] };
				return Response.json({ client_id: "ombra-test-client", redirect_uris: body.redirect_uris, token_endpoint_auth_method: "none" }, { status: 201 });
			}
			if (url.pathname === "/authorize") {
				const redirect = new URL(url.searchParams.get("redirect_uri")!);
				const code = `code-${crypto.randomUUID()}`;
				state.codes.add(code);
				redirect.searchParams.set("code", code);
				redirect.searchParams.set("state", url.searchParams.get("state") ?? "");
				return new Response(null, { status: 302, headers: { Location: redirect.href } });
			}
			if (url.pathname === "/token") {
				const form = await request.formData();
				const grant = form.get("grant_type");
				if (grant === "authorization_code" && state.codes.has(String(form.get("code")))) {
					state.issued++;
					return Response.json({ access_token: state.token, token_type: "Bearer", expires_in: 3600, refresh_token: state.refresh });
				}
				if (grant === "refresh_token" && state.refreshWorks && form.get("refresh_token") === state.refresh)
					return Response.json({ access_token: state.token, token_type: "Bearer", expires_in: 3600, refresh_token: state.refresh });
				return Response.json({ error: "invalid_grant" }, { status: 400 });
			}
			if (url.pathname === "/mcp") {
				if (request.method !== "POST") return new Response(null, { status: 405 });
				if (request.headers.get("authorization") !== `Bearer ${state.token}`)
					return new Response("unauthorized", { status: 401, headers: {
						"WWW-Authenticate": `Bearer resource_metadata="${origin}/.well-known/oauth-protected-resource/mcp"`,
					} });
				const message = await request.json() as { id?: number | string; method: string; params?: { protocolVersion?: string } };
				if (message.id === undefined) return new Response(null, { status: 202 });
				const result = message.method === "initialize"
					? { protocolVersion: message.params?.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: "oauth-test", version: "1.0.0" } }
					: message.method === "tools/list"
						? { tools: [
							{ name: "whoami", description: "Who is signed in", inputSchema: { type: "object", properties: {} } },
							{ name: "ping", description: "Ping", inputSchema: { type: "object", properties: {} } },
						] }
						: undefined;
				if (!result) return Response.json({ jsonrpc: "2.0", id: message.id, error: { code: -32601, message: "Method not found" } });
				return Response.json({ jsonrpc: "2.0", id: message.id, result });
			}
			return new Response("not found", { status: 404 });
		},
	});
	cleanups.push(() => { server.stop(true); });
	return { url: `http://127.0.0.1:${server.port}/mcp`, state, secrets };
}

/** A stdio MCP server with no tools that asks the user for a form as soon as it is initialized. */
const ELICITING_SERVER = `
const readline = require("node:readline");
const fs = require("node:fs");
const out = process.env.ELICIT_OUT;
let caps = {};
const send = message => process.stdout.write(JSON.stringify(message) + "\\n");
readline.createInterface({ input: process.stdin }).on("line", line => {
	const message = JSON.parse(line);
	if (message.method === "initialize") {
		caps = message.params.capabilities || {};
		fs.writeFileSync(out + ".caps", JSON.stringify(caps));
		send({ jsonrpc: "2.0", id: message.id, result: { protocolVersion: message.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: "asker", version: "1" } } });
	} else if (message.method === "notifications/initialized") {
		if (caps.elicitation) send({ jsonrpc: "2.0", id: "ask-1", method: "elicitation/create", params: {
			message: "Where should I deploy?",
			requestedSchema: { type: "object", required: ["env"], properties: {
				env: { type: "string", title: "Environment", enum: ["staging", "prod"], enumNames: ["Staging", "Production"] },
				replicas: { type: "integer", title: "Replicas", minimum: 1, maximum: 5 },
				notify: { type: "boolean", title: "Notify the team", default: true },
				email: { type: "string", format: "email", description: "Who to tell" },
			} },
		} });
	} else if (message.id === "ask-1") {
		fs.appendFileSync(out, JSON.stringify(message.result ?? { error: message.error }) + "\\n");
	} else if (message.method === "tools/list") {
		send({ jsonrpc: "2.0", id: message.id, result: { tools: [] } });
	} else if (message.id !== undefined && message.method) {
		send({ jsonrpc: "2.0", id: message.id, result: {} });
	}
});
`;

async function fixture(servers: Record<string, object>) {
	await mkdir(runRoot, { recursive: true });
	const root = await mkdtemp(join(runRoot, "case-"));
	const agentDir = join(root, "agent");
	const project = join(root, "project");
	await mkdir(agentDir, { recursive: true });
	await mkdir(project, { recursive: true });
	await writeFile(join(agentDir, "mcp.json"), JSON.stringify({ mcpServers: servers }, null, 2));
	const env: NodeJS.ProcessEnv = { PATH: process.env.PATH, HOME: root, PI_CODING_AGENT_DIR: agentDir, PI_PROFILE: "", OMP_PROFILE: "",
		XDG_CONFIG_HOME: join(root, "config"), XDG_STATE_HOME: join(root, "state"), XDG_DATA_HOME: join(root, "data"), XDG_CACHE_HOME: join(root, "cache"),
		ELICIT_OUT: join(root, "elicited.jsonl"), PI_NOTIFICATIONS: "off" };
	const host = new Host(env, project);
	cleanups.push(() => host.stop());
	await host.wait(frame => frame.type === "ready", "ready");
	expect(await host.request("negotiate_protocol", { protocolVersion: 2 })).toMatchObject({ success: true });
	const sessionKey = "chat-1";
	const opened = await host.request("engine_open", { sessionKey, cwd: project,
		arguments: ["--no-lsp", "--no-skills", "--no-rules", "--no-prewalk", "--no-extensions"] });
	expect(opened).toMatchObject({ success: true });
	return { root, host, sessionKey, elicited: join(root, "elicited.jsonl"), agentDir };
}

async function serversOf(host: Host, key: string): Promise<Frame[]> {
	const response = await host.command(key, "get_mcp_servers");
	expect(response).toMatchObject({ success: true });
	return response.data.servers;
}

/** Polls a read with a real delay: the answers land in files a separate MCP server process writes, with no event to await. */
async function until<T>(read: () => Promise<T>, done: (value: T) => boolean, what: string, timeoutMs = 20_000): Promise<T> {
	const deadline = Date.now() + timeoutMs;
	for (;;) {
		const value = await read();
		if (done(value)) return value;
		if (Date.now() > deadline) throw new Error(`never reached: ${what}: ${JSON.stringify(value)}`);
		await Bun.sleep(100);
	}
}

/** What the user's browser does after clicking Sign in: the authorize URL, its redirect to omp's loopback callback. */
async function completeInBrowser(url: string): Promise<number> {
	const authorize = await fetch(url, { redirect: "manual" });
	expect(authorize.status).toBe(302);
	const callback = await fetch(authorize.headers.get("location")!, { redirect: "manual" });
	await callback.arrayBuffer();
	return callback.status;
}

test("sign-in: signed-out state and prompt, open_url for the server, callback, connected with tools, no secrets on the wire", async () => {
	const remote = oauthServer();
	const f = await fixture({ docs: { type: "http", url: remote.url } });

	const prompt = await f.host.waitSession(f.sessionKey, frame => frame.type === "mcp_auth_required", "auth required event");
	expect(prompt).toEqual({ type: "mcp_auth_required", serverName: "docs", reason: "signed_out" });
	const before = await serversOf(f.host, f.sessionKey);
	expect(before).toEqual([expect.objectContaining({ name: "docs", transport: "http", status: "failed", toolCount: 0, auth: "signed_out" })]);
	// Nothing opens a browser on its own: no open_url without a sign-in request.
	expect(f.host.session(f.sessionKey).some(frame => frame.method === "open_url")).toBe(false);

	const login = f.host.command(f.sessionKey, "mcp_login", { serverName: "docs" }, 60_000);
	const open = await f.host.waitSession(f.sessionKey, frame => frame.type === "extension_ui_request" && frame.method === "open_url", "open_url");
	expect(open.mcpServer).toBe("docs");
	expect(open.url.startsWith(remote.url.replace("/mcp", "/authorize?"))).toBe(true);
	// A command sent while the browser step waits is answered: the sign-in does not hold the session's queue.
	expect((await f.host.command(f.sessionKey, "get_mcp_servers")).success).toBe(true);
	expect(await completeInBrowser(open.url)).toBe(200);

	const done = await login;
	expect(done).toMatchObject({ success: true, data: { name: "docs", status: "connected", toolCount: 2, auth: "signed_in" } });
	expect(await serversOf(f.host, f.sessionKey)).toEqual([expect.objectContaining({ name: "docs", status: "connected", toolCount: 2, auth: "signed_in" })]);
	expect(remote.state.issued).toBe(1);
	const wire = JSON.stringify(f.host.frames) + f.host.stderr;
	for (const secret of remote.secrets()) expect(wire).not.toContain(secret);
}, 120_000);

test("expiry: a refused token with no working refresh is reported as expired and prompts again", async () => {
	const remote = oauthServer();
	const f = await fixture({ docs: { type: "http", url: remote.url } });
	await f.host.waitSession(f.sessionKey, frame => frame.type === "mcp_auth_required", "first prompt");
	const login = f.host.command(f.sessionKey, "mcp_login", { serverName: "docs" }, 60_000);
	const open = await f.host.waitSession(f.sessionKey, frame => frame.method === "open_url", "open_url");
	await completeInBrowser(open.url);
	expect((await login).success).toBe(true);

	// The provider revokes the grant: the old access token is refused and refresh fails.
	remote.state.token = `rotated-${crypto.randomUUID()}`;
	remote.state.refreshWorks = false;
	const reconnect = await f.host.command(f.sessionKey, "mcp_reconnect", { serverName: "docs" }, 60_000);
	expect(reconnect.success).toBe(true);
	const expired = await f.host.waitSession(f.sessionKey, frame => frame.type === "mcp_auth_required" && frame.reason === "expired", "expiry prompt");
	expect(expired).toEqual({ type: "mcp_auth_required", serverName: "docs", reason: "expired" });
	expect(await serversOf(f.host, f.sessionKey)).toEqual([expect.objectContaining({ name: "docs", auth: "expired" })]);
	expect(f.host.session(f.sessionKey).filter(frame => frame.method === "open_url")).toHaveLength(1);
}, 120_000);

test("sign-in refuses a non-HTTPS, non-loopback authorization URL and can be cancelled", async () => {
	const evil = oauthServer({ authorizeEndpoint: () => "http://auth.example.com/authorize" });
	const good = oauthServer();
	const f = await fixture({ evil: { type: "http", url: evil.url }, slow: { type: "http", url: good.url } });

	const refused = await f.host.command(f.sessionKey, "mcp_login", { serverName: "evil" }, 60_000);
	expect(refused.success).toBe(false);
	expect(refused.error).toContain("https");
	expect(f.host.session(f.sessionKey).some(frame => frame.method === "open_url")).toBe(false);

	const login = f.host.command(f.sessionKey, "mcp_login", { serverName: "slow" }, 60_000);
	await f.host.waitSession(f.sessionKey, frame => frame.method === "open_url" && frame.mcpServer === "slow", "open_url");
	expect(await f.host.command(f.sessionKey, "mcp_login_cancel", { serverName: "slow" })).toMatchObject({ success: true, data: { cancelled: true } });
	expect(await login).toMatchObject({ success: false, code: "mcp_login_cancelled" });
	expect(await f.host.command(f.sessionKey, "mcp_login", { serverName: "nope" })).toMatchObject({ success: false });
	expect(good.state.issued).toBe(0);
}, 120_000);

test("elicitation: a server's form arrives as an elicit UI request and the user's answer is validated and returned", async () => {
	const f = await fixture({ asker: { type: "stdio", command: process.execPath, args: ["-e", ELICITING_SERVER] } });
	const ask = await f.host.waitSession(f.sessionKey, frame => frame.type === "extension_ui_request" && frame.method === "elicit", "elicit request");
	expect(ask).toMatchObject({ serverName: "asker", message: "Where should I deploy?" });
	expect(ask.fields).toEqual([
		{ name: "env", kind: "select", title: "Environment", required: true, options: [{ value: "staging", label: "Staging" }, { value: "prod", label: "Production" }] },
		{ name: "replicas", kind: "integer", title: "Replicas", required: false, minimum: 1, maximum: 5 },
		{ name: "notify", kind: "boolean", title: "Notify the team", required: false, default: true },
		{ name: "email", kind: "text", title: "email", description: "Who to tell", required: false, format: "email" },
	]);
	expect(JSON.parse(await readFile(`${f.elicited}.caps`, "utf8"))).toMatchObject({ elicitation: { form: {} } });

	// An answer that breaks the form (unknown option) never reaches the server as an acceptance.
	f.host.sendSession(f.sessionKey, { type: "extension_ui_response", id: ask.id, action: "accept", content: { env: "qa" } });
	const first = await until(() => readFile(f.elicited, "utf8").catch(() => ""), text => text.length > 0, "server got an answer");
	expect(JSON.parse(first.trim())).toMatchObject({ error: { code: -32602 } });

	// A connected server without tools is listed as connected with 0 tools (the client warns about it).
	expect(await serversOf(f.host, f.sessionKey)).toEqual([expect.objectContaining({ name: "asker", transport: "stdio", status: "connected", toolCount: 0, auth: "none" })]);

	// Reconnect all: the server starts again, asks again; this time the user accepts a valid form.
	const reconnect = await f.host.command(f.sessionKey, "mcp_reconnect", {}, 60_000);
	expect(reconnect).toMatchObject({ success: true, data: { servers: [expect.objectContaining({ name: "asker" })] } });
	const again = await f.host.waitSession(f.sessionKey, frame => frame.method === "elicit" && frame.id !== ask.id, "second elicit");
	f.host.sendSession(f.sessionKey, { type: "extension_ui_response", id: again.id, action: "accept",
		content: { env: "prod", replicas: 2, notify: false, email: "ops@example.com" } });
	const both = await until(() => readFile(f.elicited, "utf8"), text => text.trim().split("\n").length >= 2, "second answer");
	expect(JSON.parse(both.trim().split("\n")[1]!)).toEqual({ action: "accept", content: { env: "prod", replicas: 2, notify: false, email: "ops@example.com" } });
}, 120_000);
