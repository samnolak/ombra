import { afterAll, afterEach, expect, test } from "bun:test";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { mkdtemp, mkdir, readFile, writeFile, rm, symlink, link, rename, stat } from "node:fs/promises";
import { join, resolve } from "node:path";

// Runtime-selected staging keeps canonical sources paired with the pinned native dependencies.
const engineRoot = process.env.OMBRA_ENGINE_ROOT;
if (!engineRoot) throw new Error("Set OMBRA_ENGINE_ROOT to a staged runtime/engine with the pinned omp/node_modules linked");
if (Bun.version !== "1.4.2") throw new Error(`Use the pack's pinned Bun 1.4.2, not ${Bun.version}`);
const children: Peer[] = [];
const roots: string[] = [];
const runRoot = resolve(import.meta.dir, "../../.temp/ombra-tests", `run-${process.pid}`);
type Holder = { id: string; kind: string; pid: number; label?: string; since: string };
type Frame = {
	type?: string; id?: string; success?: boolean; error?: string; code?: string; path?: string; sessionId?: string;
	sessionFile?: string; source?: string; claimedDuringClose?: boolean; sessionKey?: string; frame?: Frame;
	data?: { sessionId?: string; sessionFile?: string; holder?: Holder | null };
	notices?: { reason: string; from: string; to: string }[]; holder?: Holder | null;
};

class Peer {
	readonly process;
	readonly frames: Frame[] = [];
	readonly exited: Promise<number | null>;
	#sequence = 0;
	#stderr = "";
	#changed = new Set<() => void>();
	constructor(args: string[], env: NodeJS.ProcessEnv, cwd: string) {
		this.process = spawn(process.execPath, args, { cwd, env, stdio: ["pipe", "pipe", "pipe"] });
		const exited = Promise.withResolvers<number | null>();
		this.process.once("exit", exited.resolve);
		this.exited = exited.promise;
		this.process.stderr.on("data", data => { this.#stderr = (this.#stderr + data).slice(-8192); });
		createInterface({ input: this.process.stdout }).on("line", line => {
			this.frames.push(JSON.parse(line));
			for (const wake of this.#changed) wake();
		});
		children.push(this);
	}
	async wait(predicate: (frame: Frame) => boolean, timeoutMs = 20_000): Promise<Frame> {
		const pending = Promise.withResolvers<Frame>();
		const finish = () => {
			const frame = this.frames.find(predicate);
			if (frame) { clearTimeout(timer); this.#changed.delete(finish); pending.resolve(frame); }
		};
		// Integration deadline only: success is driven by an actual pipe frame, never a guessed delay.
		const timer = setTimeout(() => { this.#changed.delete(finish); pending.reject(new Error(`Process response timeout: ${this.#stderr}`)); }, timeoutMs);
		this.#changed.add(finish);
		finish();
		return pending.promise;
	}
	async request(type: string, fields: object = {}, timeoutMs?: number): Promise<Frame> {
		const id = `request-${++this.#sequence}`;
		this.process.stdin.write(`${JSON.stringify({ ...fields, type, id })}\n`);
		return this.wait(frame => frame.id === id, timeoutMs);
	}
	async sessionRequest(sessionKey: string, type: string, fields: object = {}): Promise<Frame> {
		const id = `session-request-${++this.#sequence}`;
		this.process.stdin.write(`${JSON.stringify({ type: "engine_session_command", sessionKey, frame: { ...fields, type, id } })}\n`);
		return (await this.wait(frame => frame.sessionKey === sessionKey && frame.frame?.id === id)).frame!;
	}
	async stop(): Promise<void> {
		if (this.process.exitCode !== null || this.process.signalCode !== null) return;
		if (process.platform !== "win32") this.process.kill("SIGCONT");
		this.process.kill("SIGKILL");
		await this.exited;
	}
}

async function fixture() {
	await mkdir(runRoot, { recursive: true });
	const root = await mkdtemp(join(runRoot, "session-ownership-"));
	roots.push(root);
	const file = join(root, "original.jsonl");
	const sessionId = crypto.randomUUID();
	await writeFile(file, JSON.stringify({ type: "session", version: 3, id: sessionId, cwd: root, timestamp: new Date().toISOString() }) + "\n");
	await mkdir(file.slice(0, -6));
	await writeFile(join(file.slice(0, -6), "artifact.txt"), "retained artifact");
	const env = { ...process.env, HOME: root, PI_CODING_AGENT_DIR: join(root, "agent"), PI_PROFILE: "", OMP_PROFILE: "",
		XDG_CONFIG_HOME: join(root, "config"), XDG_STATE_HOME: join(root, "state"), XDG_DATA_HOME: join(root, "data"), XDG_CACHE_HOME: join(root, "cache"),
		PI_TEST_SESSION_OWNERS_DIR: join(root, "owners"), OMBRA_ENGINE_ROOT: resolve(engineRoot!) };
	const worker = async (target = file, extraEnv: object = {}) => {
		const peer = new Peer([join(import.meta.dir, "session-ownership-worker.ts"), target], { ...env, ...extraEnv }, root);
		await peer.wait(frame => frame.type === "ready");
		return peer;
	};
	return { root, file, sessionId, env, worker };
}

afterEach(async () => {
	await Promise.all(children.splice(0).map(child => child.stop()));
	await Promise.all(roots.splice(0).map(root => rm(root, { recursive: true, force: true })));
});

afterAll(() => rm(runRoot, { recursive: true, force: true }));

test("read-only inspection does not claim; close holds through its tail and revival reacquires", async () => {
	const f = await fixture();
	const reader = await f.worker();
	expect((await reader.request("read")).success).toBe(true);
	const writer = await f.worker();
	const first = await writer.request("append", { marker: "first writer" });
	expect(first).toMatchObject({ success: true, path: f.file, sessionId: f.sessionId, notices: [] });
	expect(await writer.request("close_probe")).toMatchObject({ success: true, claimedDuringClose: false });
	const revived = await reader.request("append", { marker: "revived writer" });
	expect(revived).toMatchObject({ success: true, path: f.file, sessionId: f.sessionId, notices: [] });
	const oldWriter = await writer.request("append", { marker: "old writer now forks" });
	expect(oldWriter.success).toBe(true);
	expect(oldWriter.path).not.toBe(f.file);
	expect(oldWriter.notices).toContainEqual({ reason: "open-elsewhere", from: f.file, to: oldWriter.path! });
	const original = await readFile(f.file, "utf8");
	expect(original).toContain("revived writer");
	expect(original).not.toContain("old writer now forks");
}, 60_000);

test("competitor cannot rewrite, move or delete an owned journal; title mutation explicitly forks", async () => {
	const f = await fixture();
	const owner = await f.worker();
	expect((await owner.request("append", { marker: "owner journal" })).success).toBe(true);
	const original = await readFile(f.file, "utf8");
	const contender = await f.worker();
	const denied = await contender.request("delete", { path: f.file });
	expect(denied.success).toBe(false);
	expect(denied.error).toContain("busy");
	const destination = join(f.root, "moved");
	await mkdir(destination);
	expect((await contender.request("move", { cwd: destination, directory: destination })).success).toBe(false);
	const fork = await contender.request("rename", { name: "competitor title" });
	expect(fork.success).toBe(true);
	expect(fork.path).not.toBe(f.file);
	expect(fork.sessionId).not.toBe(f.sessionId);
	expect(fork.notices).toContainEqual({ reason: "open-elsewhere", from: f.file, to: fork.path! });
	expect(await readFile(f.file, "utf8")).toBe(original);
	expect(await readFile(join(f.file.slice(0, -6), "artifact.txt"), "utf8")).toBe("retained artifact");
}, 60_000);

test.skipIf(process.platform === "win32")("SIGSTOP cannot expire ownership; SIGKILL releases without PID/timeout theft", async () => {
	const f = await fixture();
	const owner = await f.worker();
	expect((await owner.request("append", { marker: "paused owner" })).success).toBe(true);
	owner.process.kill("SIGSTOP");
	// Real OS-process pause beyond the lock retry window: fake JS timers cannot advance a native lease.
	await Bun.sleep(6_500);
	const contender = await f.worker();
	expect((await contender.request("delete", { path: f.file })).success).toBe(false);
	owner.process.kill("SIGKILL");
	await owner.exited;
	const acquired = await contender.request("append", { marker: "after crash" });
	expect(acquired).toMatchObject({ success: true, path: f.file, sessionId: f.sessionId, notices: [] });
}, 60_000);

test.skipIf(process.platform === "win32")("symlink, hard link and renamed journal retain the same session-id ownership", async () => {
	const f = await fixture();
	const owner = await f.worker();
	expect((await owner.request("append", { marker: "owner" })).success).toBe(true);
	const alias = join(f.root, "alias.jsonl");
	await symlink(f.file, alias);
	const aliasReader = await f.worker(alias);
	expect((await aliasReader.request("delete", { path: alias })).success).toBe(false);
	const hardLink = join(f.root, "hard-link.jsonl");
	await link(f.file, hardLink);
	expect((await aliasReader.request("delete", { path: hardLink })).success).toBe(false);
	const moved = join(f.root, "renamed.jsonl");
	await rename(f.file, moved);
	const movedReader = await f.worker(moved);
	expect((await movedReader.request("delete", { path: moved })).success).toBe(false);
	expect((await stat(moved)).isFile()).toBe(true);
}, 60_000);

test.skipIf(process.platform === "linux" || process.platform === "win32")("OS lease setup errors fail closed, not as a local-only claim", async () => {
	const f = await fixture();
	const blockedDir = join(f.root, "not-a-directory");
	await writeFile(blockedDir, "blocks mkdir");
	const original = await readFile(f.file, "utf8");
	const writer = await f.worker(f.file, { PI_TEST_SESSION_OWNERS_DIR: blockedDir });
	const result = await writer.request("append", { marker: "must never land" });
	expect(result.success).toBe(false);
	expect(result.notices).toEqual([]);
	expect(await readFile(f.file, "utf8")).toBe(original);
	expect((await writer.request("delete", { path: f.file })).success).toBe(false);
}, 60_000);

test("real shared RPC root refuses a live CLI-manager owner, then deletes after ordinary close", async () => {
	const f = await fixture();
	const owner = await f.worker();
	expect((await owner.request("append", { marker: "CLI lifetime" })).success).toBe(true);
	const host = new Peer([join(engineRoot!, "host.ts")], f.env, f.root);
	await host.wait(frame => frame.type === "ready");
	expect((await host.request("negotiate_protocol", { protocolVersion: 2 })).success).toBe(true);
	const refused = await host.request("engine_delete_session", { path: f.file });
	expect(refused.success).toBe(false);
	expect(refused.error).toContain("busy");
	expect((await stat(f.file)).isFile()).toBe(true);
	expect((await owner.request("close")).success).toBe(true);
	expect((await host.request("engine_delete_session", { path: f.file })).success).toBe(true);
	expect(await Bun.file(f.file).exists()).toBe(false);
	expect(await Bun.file(join(f.file.slice(0, -6), "artifact.txt")).exists()).toBe(false);
	expect((await host.request("engine_shutdown")).success).toBe(true);
}, 60_000);

const RESUME_FLAGS = ["--no-tools", "--no-lsp", "--no-extensions", "--no-skills", "--no-rules", "--no-prewalk", "--no-ui"];

async function engineHost(f: { env: NodeJS.ProcessEnv; root: string }): Promise<Peer> {
	const host = new Peer([join(engineRoot!, "host.ts")], f.env, f.root);
	await host.wait(frame => frame.type === "ready");
	expect((await host.request("negotiate_protocol", { protocolVersion: 2 })).success).toBe(true);
	return host;
}

test("shared RPC open of a chat another writer holds is refused with that holder; Open here makes it yield", async () => {
	const f = await fixture();
	const owner = await f.worker();
	expect((await owner.request("append", { marker: "terminal owner" })).success).toBe(true);
	const original = await readFile(f.file, "utf8");
	const host = await engineHost(f);
	const refused = await host.request("engine_open", { sessionKey: "gui", cwd: f.root, arguments: ["--resume", f.file, ...RESUME_FLAGS] });
	expect(refused).toMatchObject({ success: false, code: "session_lease_held", data: { sessionId: f.sessionId } });
	expect(refused.data?.holder).toMatchObject({ kind: "omp", pid: owner.process.pid });
	// The refused reader wrote nothing, not even close-time bookkeeping.
	expect(await readFile(f.file, "utf8")).toBe(original);

	// "Open here": the terminal's manager finishes its writes and yields; it never loses an entry.
	expect(await host.request("engine_take_session", { path: f.file })).toMatchObject({ success: true });
	expect((await host.request("engine_open", { sessionKey: "gui-2", cwd: f.root, arguments: ["--resume", f.file, ...RESUME_FLAGS] })).success).toBe(true);
	const state = await host.sessionRequest("gui-2", "get_state");
	expect(state.data).toMatchObject({ sessionFile: f.file, sessionId: f.sessionId });
	expect((await host.sessionRequest("gui-2", "set_session_name", { name: "Opened here" })).success).toBe(true);
	expect(await readFile(f.file, "utf8")).toContain("Opened here");

	// The terminal keeps working, in a sibling: the journal never gets a second writer.
	const moved = await owner.request("append", { marker: "terminal after takeover" });
	expect(moved.success).toBe(true);
	expect(moved.path).not.toBe(f.file);
	expect(moved.notices).toContainEqual({ reason: "open-elsewhere", from: f.file, to: moved.path! });
	expect(await readFile(f.file, "utf8")).not.toContain("terminal after takeover");
}, 60_000);

test("two logical chats of one engine: the second is refused until it takes over; the first is told and closes", async () => {
	const f = await fixture();
	const host = await engineHost(f);
	expect((await host.request("engine_open", { sessionKey: "a", cwd: f.root, arguments: ["--resume", f.file, ...RESUME_FLAGS] })).success).toBe(true);
	const refused = await host.request("engine_open", { sessionKey: "b", cwd: f.root, arguments: ["--resume", f.file, ...RESUME_FLAGS] });
	expect(refused).toMatchObject({ success: false, code: "session_lease_held" });
	expect(refused.data?.holder).toMatchObject({ kind: "ombra", label: "Ombra", pid: host.process.pid });
	// A same-folder switch onto the held chat is refused the same way.
	expect((await host.request("engine_open", { sessionKey: "c", cwd: f.root, arguments: RESUME_FLAGS })).success).toBe(true);
	expect(await host.sessionRequest("c", "switch_session", { sessionPath: f.file })).toMatchObject({ success: false, code: "session_lease_held" });

	expect(await host.request("engine_take_session", { path: f.file })).toMatchObject({ success: true });
	const lost = await host.wait(frame => frame.sessionKey === "a" && frame.frame?.type === "session_lease_lost");
	expect(lost.frame?.holder).toMatchObject({ kind: "ombra", pid: host.process.pid });
	await host.wait(frame => frame.type === "engine_session_closed" && frame.sessionKey === "a");
	expect(await host.sessionRequest("c", "switch_session", { sessionPath: f.file })).toMatchObject({ success: true });
	const state = await host.sessionRequest("c", "get_state");
	expect(state.data).toMatchObject({ sessionFile: f.file, sessionId: f.sessionId });
}, 60_000);

test.skipIf(process.platform === "win32")("Open here never takes a lease by force from a holder that does not answer", async () => {
	const f = await fixture();
	const owner = await f.worker();
	expect((await owner.request("append", { marker: "paused owner" })).success).toBe(true);
	const original = await readFile(f.file, "utf8");
	owner.process.kill("SIGSTOP");
	const host = await engineHost(f);
	// The engine waits its full 30 s (SESSION_TAKEOVER_WAIT_MS) for a holder that never answers.
	const refused = await host.request("engine_take_session", { path: f.file }, 40_000);
	expect(refused).toMatchObject({ success: false, code: "session_lease_held" });
	expect(refused.data?.holder).toMatchObject({ kind: "omp", pid: owner.process.pid });
	expect(await readFile(f.file, "utf8")).toBe(original);
	owner.process.kill("SIGCONT");
	// The withdrawn request is not answered late: the owner still writes its own journal.
	expect(await owner.request("append", { marker: "owner after refusal" })).toMatchObject({ success: true, path: f.file, notices: [] });
}, 60_000);

test("deleting an inactive parent cannot remove a live nested journal with its artifacts", async () => {
	const f = await fixture();
	const nestedFile = join(f.file.slice(0, -6), "nested.jsonl");
	await writeFile(nestedFile, JSON.stringify({
		type: "session", version: 3, id: crypto.randomUUID(), cwd: f.root, timestamp: new Date().toISOString(),
	}) + "\n");
	const nested = await f.worker(nestedFile);
	expect((await nested.request("append", { marker: "live nested writer" })).success).toBe(true);
	const deleter = await f.worker();
	const denied = await deleter.request("delete", { path: f.file });
	expect(denied.success).toBe(false);
	expect(denied.error).toContain("busy");
	expect(await Bun.file(f.file).exists()).toBe(true);
	expect(await Bun.file(nestedFile).exists()).toBe(true);
	expect((await nested.request("close")).success).toBe(true);
	expect((await deleter.request("delete", { path: f.file })).success).toBe(true);
	expect(await Bun.file(nestedFile).exists()).toBe(false);
}, 60_000);
