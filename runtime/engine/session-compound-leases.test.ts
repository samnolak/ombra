import { afterAll, beforeAll, describe, expect, spyOn, test } from "bun:test";
import { Database } from "bun:sqlite";
import { copyFile, mkdir, mkdtemp, readFile, rm, unlink } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { Agent } from "@oh-my-pi/pi-agent-core";
import { getBundledModel } from "@oh-my-pi/pi-catalog/models";
import { getAgentDir, refreshDirsFromEnv, setAgentDir } from "@oh-my-pi/pi-utils";
import { CollabGuestLink } from "./coding-agent/src/collab/guest";
import { COLLAB_PROTO, type CollabSessionState } from "./coding-agent/src/collab/protocol";
import { CollabSocket } from "./coding-agent/src/collab/relay-client";
import { ModelRegistry } from "./coding-agent/src/config/model-registry";
import { Settings } from "./coding-agent/src/config/settings";
import type { InteractiveModeContext } from "./coding-agent/src/modes/types";
import { AgentSession } from "./coding-agent/src/session/agent-session";
import { AuthStorage, SqliteAuthCredentialStore } from "./coding-agent/src/session/auth-storage";
import { closeSessionIndex } from "./coding-agent/src/session/session-index";
import { loadEntriesFromFile } from "./coding-agent/src/session/session-loader";
import { SessionManager } from "./coding-agent/src/session/session-manager";
import { FileSessionStorage, SessionLeaseAuthority, isSessionLeaseFree } from "./coding-agent/src/session/session-storage";
import { forkIfMovedAway, lastSessionTransfer, markSessionMoved, packSession, unpackSession } from "./coding-agent/src/session/session-transfer";

// Real session persistence and real guest snapshot adoption. Only the transport
// and terminal UI are replaced: no provider, relay server or user profile is used.
describe("compound session writer ownership", () => {
	let root: string;
	let originalAgentDir: string;
	let originalAgentEnv: string | undefined;
	let restoreHome: () => void;

	beforeAll(async () => {
		root = await mkdtemp(path.join(os.tmpdir(), "ombra-compound-leases-"));
		originalAgentDir = getAgentDir();
		originalAgentEnv = process.env.PI_CODING_AGENT_DIR;
		const home = spyOn(os, "homedir").mockReturnValue(root);
		restoreHome = () => home.mockRestore();
		refreshDirsFromEnv();
		setAgentDir(path.join(root, "agent"));
	});

	afterAll(async () => {
		closeSessionIndex();
		restoreHome();
		refreshDirsFromEnv();
		setAgentDir(originalAgentDir);
		if (originalAgentEnv === undefined) delete process.env.PI_CODING_AGENT_DIR;
		else process.env.PI_CODING_AGENT_DIR = originalAgentEnv;
		await rm(root, { recursive: true, force: true });
	});

	async function journal(name: string): Promise<string> {
		const cwd = path.join(root, name);
		await mkdir(cwd, { recursive: true });
		const manager = SessionManager.create(cwd, path.join(cwd, "sessions"));
		try {
			manager.appendMessage({ role: "user", content: "first", timestamp: 1 });
			await manager.ensureOnDisk();
			return manager.getSessionFile()!;
		} finally {
			await manager.close();
		}
	}

	test("delegation keeps one lease across reloads, without authorizing independent managers", async () => {
		const file = await journal("authority");
		const manager = await SessionManager.open(file);
		const id = manager.getSessionId();
		const authority = SessionLeaseAuthority.acquire(id, file)!;
		const contender = await SessionManager.open(file);
		try {
			expect(authority).not.toBeNull();
			expect(manager.claimWriterLease(authority)).toBe(true);
			expect(contender.claimWriterLease()).toBe(false);
			expect(authority.claimSession(crypto.randomUUID(), file)).toBeNull();
			await expect(new FileSessionStorage().deleteSessionWithArtifacts(file)).rejects.toThrow("busy");
			manager.appendMessage({ role: "user", content: "delegated write", timestamp: 2 });
			await manager.flush();
			await manager.setSessionFile(file, { writerLease: authority });
			expect(manager.getSessionId()).toBe(id);
			expect(isSessionLeaseFree(id)).toBe(false);
			// Ending the operation cannot release an already-borrowed writer early.
			authority.release();
			expect(authority.claimSession(id, file)).toBeNull();
			expect(contender.claimWriterLease()).toBe(false);
			await manager.close();
			expect(contender.claimWriterLease()).toBe(true);
			expect(await readFile(file, "utf8")).toContain("delegated write");
		} finally {
			await manager.close();
			await contender.close();
			authority.release();
		}
	});

	test("marking moved writes the original journal and stale resume forks it", async () => {
		const file = await journal("departed");
		await markSessionMoved(file, "cloud");
		const entries = await loadEntriesFromFile(file);
		expect(lastSessionTransfer(entries)).toEqual({ kind: "departed", to: "cloud" });
		const before = await readFile(file, "utf8");
		const resumed = await forkIfMovedAway(await SessionManager.open(file), path.dirname(file));
		try {
			expect(resumed.getSessionFile()).not.toBe(file);
			expect(resumed.getHeader()?.parentSession).toBe(entries[0]?.id);
			expect(lastSessionTransfer(resumed.getEntries())).toEqual({ kind: "forked", source: file, to: "cloud" });
			expect(await readFile(file, "utf8")).toBe(before);
		} finally {
			await resumed.close();
		}
	});

	test("unpack replaces a stale local prefix under the same operation lease", async () => {
		const localFile = await journal("returning");
		const cloudFile = path.join(root, "cloud-copy.jsonl");
		await copyFile(localFile, cloudFile);
		const cloud = await SessionManager.open(cloudFile);
		const id = cloud.getSessionId();
		try {
			cloud.appendMessage({ role: "user", content: "cloud work", timestamp: 2 });
			await cloud.flush();
		} finally {
			await cloud.close();
		}
		const archive = path.join(root, "return.tar");
		await packSession(cloudFile, archive);
		await unlink(cloudFile);
		await markSessionMoved(localFile, "cloud");
		const cwd = path.dirname(path.dirname(localFile));
		const placed = await unpackSession(archive, { cwd, sessionDir: path.dirname(localFile) });
		expect(placed).toMatchObject({ id, mode: "replaced" });
		const persisted = await readFile(placed.sessionFile, "utf8");
		expect(persisted).toContain("cloud work");
		expect(lastSessionTransfer(await loadEntriesFromFile(placed.sessionFile))?.kind).toBe("arrived");
		expect(isSessionLeaseFree(id)).toBe(true);
	});

	test("a live independent writer still refuses transfer and leaves its journal unchanged", async () => {
		const file = await journal("busy-transfer");
		const owner = await SessionManager.open(file);
		try {
			expect(owner.claimWriterLease()).toBe(true);
			const before = await readFile(file, "utf8");
			await expect(markSessionMoved(file, "cloud")).rejects.toThrow("another live omp process");
			await expect(packSession(file, path.join(root, "busy.tar"))).rejects.toThrow("another live omp process");
			expect(await readFile(file, "utf8")).toBe(before);
		} finally {
			await owner.close();
		}
	});

	test("a real collab guest keeps its replica id and continuous ownership after live entries and resync", async () => {
		const localFile = await journal("guest");
		const manager = await SessionManager.open(localFile);
		const model = getBundledModel("anthropic", "claude-sonnet-4-5")!;
		const auth = new AuthStorage(new SqliteAuthCredentialStore(new Database(":memory:")));
		auth.keys.setRuntime("anthropic", "test-key");
		const session = new AgentSession({
			agent: new Agent({ initialState: { model, systemPrompt: ["Test"], tools: [], messages: [] } }),
			sessionManager: manager,
			settings: Settings.isolated(),
			modelRegistry: new ModelRegistry(auth),
		});
		const ready = Promise.withResolvers<CollabSocket>();
		const connect = spyOn(CollabSocket.prototype, "connect").mockImplementation(function (this: CollabSocket) {
			ready.resolve(this);
		});
		const resynced = Promise.withResolvers<void>();
		const noop = () => {};
		const ctx = {
			settings: session.settings, sessionManager: manager, session,
			statusContainer: { clear: noop, disposeChildren: noop },
			pendingMessagesContainer: { clear: noop }, compactionQueuedMessages: [], pendingTools: new Map(),
			ensureLoadingAnimation: noop,
			statusLine: { setCollabStatus: noop, invalidate: noop, markActivityStart: noop, markActivityEnd: noop,
				getCachedContextBreakdown: () => ({ usedTokens: 0, contextWindow: 0 }), resetActiveTime: noop },
			ui: { requestRender: noop }, chatContainer: { clear: noop, disposeChildren: noop },
			resetObserverRegistry: noop, syncRunningSubagentBadge: noop,
			renderInitialMessages: async () => {}, reloadTodos: async () => {},
			showStatus: (message: string) => { if (message.startsWith("Reconnected")) resynced.resolve(); },
			showError: (message: string) => resynced.reject(new Error(message)),
			eventController: { dispatchSessionEvent: async () => {}, takeDisplaceableComponents: () => [], resetTranscriptAnchors: noop },
			handleResumeSession: (file: string) => session.switchSession(file, { preserveLocalCwd: true, keepModel: true }),
		} as unknown as InteractiveModeContext;
		const guest = new CollabGuestLink(ctx);
		const host = SessionManager.inMemory(root);
		host.appendMessage({ role: "user", content: "first", timestamp: 1 });
		const kept = host.appendMessage({ role: "user", content: "keep", timestamp: 2 });
		let replicaId: string | undefined;
		try {
			const joined = guest.join(`ws://localhost:8788/r/ownership-room.${Buffer.alloc(32, 1).toString("base64url")}`);
			const socket = await ready.promise;
			const snapshot = () => {
				socket.onFrame!({ t: "welcome", proto: COLLAB_PROTO, header: host.getHeader()!,
					state: { cwd: root, isStreaming: false, queuedMessageCount: 0, participants: [] } as unknown as CollabSessionState,
					agents: [], entryCount: host.getEntries().length }, 0);
				socket.onFrame!({ t: "snapshot-chunk", entries: host.getEntries(), final: true }, 0);
			};
			snapshot();
			await joined;
			replicaId = manager.getSessionId();
			const replicaFile = manager.getSessionFile()!;
			expect(replicaId).not.toBe(host.getSessionId());
			expect(manager.getHeader()?.parentSession).toBe(host.getSessionId());
			expect(isSessionLeaseFree(replicaId)).toBe(false);
			host.appendMessage({ role: "user", content: "live entry", timestamp: 3 });
			socket.onFrame!({ t: "entry", entry: host.getEntries().at(-1)! }, 0);
			host.appendCompaction("SUMMARY", undefined, kept, 100);
			snapshot();
			await resynced.promise;
			expect(session.messages[0]?.role).toBe("compactionSummary");
			expect(manager.getSessionId()).toBe(replicaId);
			expect(manager.getSessionFile()).toBe(replicaFile);
			expect(isSessionLeaseFree(replicaId)).toBe(false);
			const contender = await SessionManager.open(replicaFile);
			try { expect(contender.claimWriterLease()).toBe(false); }
			finally { await contender.close(); }
			await guest.leave("finished");
			expect(manager.getSessionFile()).toBe(localFile);
			expect(isSessionLeaseFree(replicaId)).toBe(true);
		} finally {
			await guest.leave("cleanup");
			await session.dispose();
			auth.close();
			connect.mockRestore();
		}
	}, 20_000);
});
