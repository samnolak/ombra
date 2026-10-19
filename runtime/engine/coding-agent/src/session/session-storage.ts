import * as fs from "node:fs";
import * as fsp from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { FileLock as NativeFileLock } from "@oh-my-pi/pi-natives";
import { getSessionOwnersDir } from "@oh-my-pi/pi-utils/dirs";
import { isBunTestRuntime } from "@oh-my-pi/pi-utils/env";
import { type FileLockHandle, tryAcquireFileLock, withFileLockSync } from "@oh-my-pi/pi-utils/file-lock";
import { type FsError, hasFsCode, isEnoent } from "@oh-my-pi/pi-utils/fs-error";
import { openCloexecSync } from "@oh-my-pi/pi-utils/fs-open";
import * as logger from "@oh-my-pi/pi-utils/logger";
import { peekFileEnds } from "@oh-my-pi/pi-utils/peek-file";
import { Snowflake } from "@oh-my-pi/pi-utils/snowflake";
import { isRecord, toError } from "@oh-my-pi/pi-utils/type-guards";
import { isAssistantMessageLine } from "./session-entries";
import {
	overlayTitleSlotContent,
	parseTitleSlotFromContent,
	type SessionTitleUpdate,
	serializeTitleSlot,
} from "./session-title-slot";

/** Shared base flags for the held transcript descriptor; callers add `O_APPEND` or `O_TRUNC`. */
const SESSION_WRITE_FLAGS = fs.constants.O_WRONLY | fs.constants.O_CREAT;
const utf8Decoder = new TextDecoder("utf-8");

export interface SessionStorageStat {
	size: number;
	mtimeMs: number;
	mtime: Date;
}

export interface SessionStorageWriter {
	/**
	 * Append one newline-terminated line.
	 *
	 * File and memory storage apply the line synchronously before the returned
	 * promise settles, so a software crash after `append` returns (or after a
	 * fire-and-forget call begins) still sees the entry on disk / in body. No
	 * `fsync` — power loss may still drop the last page. Indexed backends update
	 * the local index immediately and queue the remote publish in call order.
	 *
	 * `line` MUST include the trailing newline.
	 */
	append(line: string): Promise<void>;
	/**
	 * Synchronous append when the backend can apply the line before return.
	 * File and memory implement this so {@link SessionManager} can latch the
	 * first write failure before the appending call returns (surfaced by a later
	 * flushSync/close/next append — the turn loop does not throw from append).
	 * Indexed backends update the local index immediately and queue remote I/O.
	 */
	appendSync?(line: string): void;
	/** Resolve once all queued appends complete. No fsync. */
	flush(): Promise<void>;
	/** Drain synchronously flushable queued work when the backend supports it. No fsync. */
	flushSync?(): void;
	/** False once close() has begun/finished. */
	isOpen(): boolean;
	close(): Promise<void>;
	getError(): Error | undefined;
}

/** Optimistic precondition for replacing a session file. */
export interface SessionStorageWriteOptions {
	/** Current UTF-8 byte length, or `null` when the target must not exist. */
	expectedSize?: number | null;
}

/**
 * The session changed after a writer loaded it, so replacing it would discard
 * another writer's durable entries.
 */
export class SessionWriteConflictError extends Error {
	readonly path: string;
	readonly expectedSize: number | null;
	readonly actualSize: number | null;

	constructor(path: string, expectedSize: number | null, actualSize: number | null) {
		const expected = expectedSize === null ? "missing" : `${expectedSize} bytes`;
		const actual = actualSize === null ? "missing" : `${actualSize} bytes`;
		super(`Session file changed before rewrite: ${path} (expected ${expected}, found ${actual}).`);
		this.name = "SessionWriteConflictError";
		this.path = path;
		this.expectedSize = expectedSize;
		this.actualSize = actualSize;
	}
}

/**
 * The file publish lock is held by another live writer, so freshness cannot
 * be established. Fail-closed: the staged rewrite is discarded without
 * publishing.
 */
export class SessionLockError extends Error {
	readonly path: string;

	constructor(path: string, detail: string) {
		super(
			`Session publish lock unavailable for ${path}: ${detail}. ` +
				`The staged rewrite was discarded without publishing.`,
		);
		this.name = "SessionLockError";
		this.path = path;
	}
}

/**
 * Optional guards applied by {@link SessionStorage.writeTextAtomic}. The
 * backend MUST check `expectedSize` and call `commitGuard()` synchronously
 * immediately before it makes the staged content visible at `path`. Failed
 * preconditions leave the target untouched. Backends MUST NOT yield between
 * the checks and publishing the write.
 */
export interface WriteTextAtomicOptions extends SessionStorageWriteOptions {
	commitGuard?: () => boolean;
}

export interface SessionStorage {
	/**
	 * `true` when synchronous writes ({@link writeTextSync} and a writer's
	 * {@link SessionStorageWriter.appendSync}) have reached the backing store by
	 * the time they return. File and memory backends apply them in-body and
	 * leave this unset. Indexed backends only update the local index and queue
	 * the remote publish, so a caller that tracks a durable byte size (such as
	 * `SessionManager`) must wait for {@link drain} to confirm before it
	 * advances that size.
	 */
	readonly defersSyncPublish?: boolean;
	/**
	 * Resolve once every write this storage queued for `path` has been confirmed
	 * by, or rejected by, the backing store. In-body backends never queue, so
	 * they omit this; {@link defersSyncPublish} marks the backends that provide
	 * it.
	 */
	confirmWrites?(path: string): Promise<void>;
	ensureDirSync(dir: string): void;
	existsSync(path: string): boolean;
	writeTextSync(path: string, content: string, options?: SessionStorageWriteOptions): void;
	/**
	 * Streaming {@link writeTextSync}: publishes the concatenation of `lines`
	 * with the same staging, size check, and conflict semantics without
	 * materializing it as one string. Optional; callers on backends without
	 * it join the lines and call {@link writeTextSync}.
	 */
	writeLinesSync?(path: string, lines: readonly string[], options?: SessionStorageWriteOptions): void;
	/**
	 * Update the current session title through the storage backend.
	 *
	 * File-like backends rewrite the fixed-width JSONL title slot; indexed
	 * backends can store the semantic title fields and synthesize the slot when
	 * reading.
	 */
	updateSessionTitle(path: string, update: SessionTitleUpdate): Promise<void>;
	statSync(path: string): SessionStorageStat;
	listFilesSync(dir: string, pattern: string): string[];

	exists(path: string): Promise<boolean>;
	readText(path: string): Promise<string>;
	/**
	 * Synchronous {@link readText}. Optional: `SessionManager` reads the file
	 * back to keep another writer's entries when a synchronous rewrite meets
	 * them, and reports the conflict instead on backends without it.
	 */
	readTextSync?(path: string): string;
	/** Read the requested UTF-8 byte windows from the head and tail of the file. */
	readTextSlices(path: string, prefixBytes: number, suffixBytes: number): Promise<[string, string]>;
	/**
	 * True when any complete `message` record in the file carries an assistant
	 * role. Scans line boundaries across the whole file (middle included) so a
	 * >prefix assistant record before a fixed-size tail window still counts.
	 * Optional: backends without cheap full scans omit it and callers fall back
	 * to prefix/suffix marker evidence.
	 */
	hasAssistantTurn?(path: string): Promise<boolean>;
	writeText(path: string, content: string): Promise<void>;
	writeTextAtomic(path: string, content: string, options?: WriteTextAtomicOptions): Promise<void>;
	/** Streaming {@link writeTextAtomic}, optional on the same terms as {@link writeLinesSync}. */
	writeLinesAtomic?(path: string, lines: readonly string[], options?: WriteTextAtomicOptions): Promise<void>;
	rename(path: string, nextPath: string): Promise<void>;
	unlink(path: string): Promise<void>;
	deleteSessionWithArtifacts(sessionPath: string): Promise<void>;
	/**
	 * Run a synchronous session mutation under the backend's cross-process
	 * lock. Optional because only backends with a process-shared lock can
	 * participate in close-time draft GC.
	 */
	withSessionFileLockSync?<T>(sessionPath: string, operation: () => T): T;
	/**
	 * Claim one writer's ownership of the session `sessionId` it writes to
	 * `sessionPath`, until the returned release callback runs. Returns `null`
	 * while another writer (including one in this process) holds the claim, or when `sessionPath` now
	 * holds a different session (another session was moved onto it), so the
	 * caller's appends would land in that session's journal; `SessionManager`
	 * then moves its session to a sibling instead of writing that file.
	 * Optional because only backends with a process-owned lock can tell that
	 * another process writes a session.
	 */
	claimSession?(sessionId: string, sessionPath: string): (() => void) | null;
	/** Who holds `sessionId`'s lease (display only); optional on the same terms as {@link claimSession}. */
	sessionLeaseHolder?(sessionId: string): SessionLeaseHolder | undefined;
	/**
	 * Call `handler` once when another writer asks for the claim this process
	 * holds on `sessionId`. Returns the unsubscribe.
	 */
	onSessionLeaseTakeover?(sessionId: string, handler: (requester: SessionLeaseHolder | undefined) => void): () => void;
	/**
	 * Atomically delete a session and its artifacts only when `shouldDelete`
	 * accepts the current session content. Optional because backends without a
	 * cross-process conditional-delete primitive must skip opportunistic GC.
	 */
	deleteSessionWithArtifactsIf?(sessionPath: string, shouldDelete: (content: string) => boolean): Promise<boolean>;
	openWriter(path: string, options?: { flags?: "a" | "w"; onError?: (err: Error) => void }): SessionStorageWriter;
	/**
	 * Wait for every backing write scheduled by this storage to become durably
	 * visible. Sync backends (file, memory) return immediately because their
	 * writes complete in-body; async backends (Redis/SQL via
	 * {@link IndexedSessionStorage}) await their per-path queues so a caller
	 * driving a graceful shutdown does not exit while a fire-and-forget
	 * `writeTextSync` publish is still on the wire.
	 */
	drain(): Promise<void>;
}

// FinalizationRegistry to clean up leaked file descriptors
const writerRegistry = new FinalizationRegistry<number>(fd => {
	try {
		fs.closeSync(fd);
	} catch {
		// Ignore - fd may already be closed or invalid
	}
});

class FileSessionStorageWriter implements SessionStorageWriter {
	#fd: number;
	/** Identity of the file `#fd` is open on: fixed for the descriptor's lifetime, so read once per descriptor. */
	#heldIdentity: { ino: bigint; dev: bigint } | undefined;
	#fpath: string;
	#publishLock: ((append: () => void, landed: () => boolean) => void) | undefined;
	#closed = false;
	#error: Error | undefined;
	#onError: ((err: Error) => void) | undefined;

	constructor(
		fpath: string,
		options?: {
			flags?: "a" | "w";
			onError?: (err: Error) => void;
			publishLock?: (append: () => void, landed: () => boolean) => void;
		},
	) {
		this.#fpath = fpath;
		this.#publishLock = options?.publishLock;
		this.#onError = options?.onError;
		const flags = options?.flags ?? "a";
		// Ensure parent directory exists
		const dir = path.dirname(fpath);
		if (!fs.existsSync(dir)) {
			fs.mkdirSync(dir, { recursive: true });
		}
		// Open file once, keep fd for lifetime
		this.#fd = openCloexecSync(
			fpath,
			SESSION_WRITE_FLAGS | (flags === "w" ? fs.constants.O_TRUNC : fs.constants.O_APPEND),
		);
		// Register for cleanup if abandoned without close()
		writerRegistry.register(this, this.#fd, this);
	}

	#heldFile(): { ino: bigint; dev: bigint } {
		if (!this.#heldIdentity) {
			const held = fs.fstatSync(this.#fd, { bigint: true });
			this.#heldIdentity = { ino: held.ino, dev: held.dev };
		}
		return this.#heldIdentity;
	}

	/**
	 * A publish that renamed a fresh file over the session path leaves this
	 * writer's descriptor on the orphaned previous inode, where the append would
	 * be silently lost. Under the publish lock no cooperating replacement can
	 * interleave, so re-open the live path when its identity changed.
	 *
	 * Returns the size of the descriptor the next write appends to: while the
	 * path still names the held file, the one path `stat` serves both the
	 * identity check and the append rollback point.
	 */
	#reopenIfReplaced(): number {
		const held = this.#heldFile();
		let live: fs.BigIntStats;
		try {
			live = fs.statSync(this.#fpath, { bigint: true });
		} catch (err) {
			if (isEnoent(err)) return fs.fstatSync(this.#fd).size;
			throw err;
		}
		if (live.ino === held.ino && live.dev === held.dev) return Number(live.size);
		const nextFd = openCloexecSync(this.#fpath, SESSION_WRITE_FLAGS | fs.constants.O_APPEND);
		writerRegistry.unregister(this);
		try {
			fs.closeSync(this.#fd);
		} catch {
			// Replacing the descriptor abandoned the old one; nothing else to do.
		}
		this.#fd = nextFd;
		this.#heldIdentity = undefined;
		writerRegistry.register(this, nextFd, this);
		const next = fs.fstatSync(nextFd, { bigint: true });
		this.#heldIdentity = { ino: next.ino, dev: next.dev };
		return Number(next.size);
	}

	/**
	 * Whether the descriptor the last append wrote through is still the file the
	 * session path names. A vanished path counts as landed: nothing replaced it,
	 * and `#reopenIfReplaced` would append to the same descriptor again.
	 */
	#holdsLivePath(): boolean {
		const held = this.#heldFile();
		let live: fs.BigIntStats;
		try {
			live = fs.statSync(this.#fpath, { bigint: true });
		} catch (err) {
			if (isEnoent(err)) return true;
			throw err;
		}
		return live.ino === held.ino && live.dev === held.dev;
	}

	#recordError(err: unknown): Error {
		const error = toError(err);
		if (!this.#error) this.#error = error;
		this.#onError?.(error);
		return error;
	}

	/** Append `line` at the end of the held descriptor, rolling back to `originalSize` on failure. */
	#writeNow(line: string, originalSize: number): void {
		const buf = Buffer.from(line, "utf-8");
		let offset = 0;
		try {
			while (offset < buf.length) {
				const written = fs.writeSync(this.#fd, buf, offset, buf.length - offset);
				if (written === 0) {
					throw new Error("Short write");
				}
				offset += written;
			}
		} catch (writeError) {
			try {
				fs.ftruncateSync(this.#fd, originalSize);
			} catch (rollbackError) {
				// Windows refuses ftruncate on an O_APPEND handle. Reopen without
				// O_APPEND and verify its identity before rolling back: the path may
				// now name a different session file after an external replacement.
				try {
					const rollbackFd = fs.openSync(this.#fpath, "r+");
					try {
						const original = fs.fstatSync(this.#fd);
						const current = fs.fstatSync(rollbackFd);
						if (original.dev !== current.dev || original.ino !== current.ino) {
							throw new Error("Session file was replaced before append rollback");
						}
						fs.ftruncateSync(rollbackFd, originalSize);
					} finally {
						fs.closeSync(rollbackFd);
					}
				} catch (pathRollbackError) {
					// Keep the write failure visible in the message.
					throw new AggregateError(
						[toError(writeError), toError(rollbackError), toError(pathRollbackError)],
						`Session append failed and its partial bytes could not be rolled back: ${toError(writeError).message}`,
					);
				}
			}
			throw writeError;
		}
	}

	appendSync(line: string): void {
		if (this.#closed) throw new Error("Writer closed");
		if (this.#error) throw this.#error;
		// Write in-body so software crash after the call still sees the entry.
		// Microtask batching used to leave completed transcript lines only in
		// memory until the next event-loop turn; process crash then lost every
		// post-checkpoint event. flush/flushSync remain no-op drains (no fsync).
		// The publish lock serializes the append against a concurrent rewrite's
		// check-then-rename, which would otherwise erase the appended turn.
		try {
			if (this.#publishLock) {
				this.#publishLock(
					() => this.#writeNow(line, this.#reopenIfReplaced()),
					() => this.#holdsLivePath(),
				);
			} else {
				this.#writeNow(line, fs.fstatSync(this.#fd).size);
			}
		} catch (err) {
			throw this.#recordError(err);
		}
	}

	async append(line: string): Promise<void> {
		this.appendSync(line);
	}

	async flush(): Promise<void> {
		if (this.#error) throw this.#error;
	}

	flushSync(): void {
		if (this.#error) throw this.#error;
	}

	isOpen(): boolean {
		return !this.#closed;
	}

	async close(): Promise<void> {
		if (this.#closed) return;
		this.#closed = true;
		// Unregister from finalization - we're closing properly
		writerRegistry.unregister(this);
		try {
			fs.closeSync(this.#fd);
		} catch {
			// Ignore close errors
		}
		if (this.#error) throw this.#error;
	}

	getError(): Error | undefined {
		return this.#error;
	}
}

/**
 * Cross-process publish serialization for the file backend. The guard-then-
 * rename sequence in `writeTextSync`/`writeTextAtomic` is synchronous (no
 * in-process interleave is possible), but a second terminal runs in another
 * process: without a shared lock its append can land between our freshness
 * check and our rename, and the rename then erases it.
 *
 * Exclusion comes in two layers. The outer layer is a process-owned OS gate
 * held from before the lockfile claim until after release: the kernel
 * reclaims it on process exit (including SIGKILL), so no wall-clock age
 * heuristic decides liveness and no unlink races ownership (F2). The inner
 * layer is the lockfile protocol below, kept so writers on a previous
 * binary still interoperate through the same file they always have; among
 * current writers the gate serializes the whole claim, which also closes
 * the two-reclaimer unlink race (rJDh). Against a previous-binary peer the
 * protocol degrades to its released semantics, documented on the steal
 * path. Appends claim the lockfile only when a previous-binary publisher
 * can be in flight (see `#withAppendLock`), so a transcript line does not
 * create and unlink a file beside the session. The window that remains is
 * non-cooperating writers (plain editors), against which the size check
 * still fails closed whenever the skew is detectable.
 *
 * The region is held for microseconds and never yields, so in-process
 * contention is impossible; cross-process contention fails closed after a
 * short bounded wait instead of blocking the turn loop.
 */
const SESSION_PUBLISH_LOCK_WAIT_MS = 500;
const SESSION_PUBLISH_LOCK_POLL_MS = 2;

const publishLockSleepBuffer = new Int32Array(new SharedArrayBuffer(4));

function sleepSyncMs(ms: number): void {
	if ("sleepSync" in Bun && typeof Bun.sleepSync === "function") {
		Bun.sleepSync(ms);
		return;
	}
	Atomics.wait(publishLockSleepBuffer, 0, 0, ms);
}

function publishLockPid(content: string): number | undefined {
	const match = /^(\d+):(\d+)\s*$/.exec(content);
	if (!match) return undefined;
	const pid = Number.parseInt(match[1], 10);
	return Number.isSafeInteger(pid) && pid > 0 ? pid : undefined;
}

function isPidAlive(pid: number): boolean {
	try {
		process.kill(pid, 0);
		return true;
	} catch (err) {
		// ESRCH: no such process (dead). EPERM: alive without signal
		// permission. Anything else: assume alive (fail closed).
		return hasFsCode(err, "EPERM") || !hasFsCode(err, "ESRCH");
	}
}

/**
 * Writer ownership leases by session id, shared by every FileSessionStorage
 * instance. A second manager in the same process must contend just like a
 * second process; sharing the OS lease would allow concurrent journal writers.
 * The OS reclaims the lease when its process exits.
 */
const sessionLeases = new Map<string, FileLockHandle>();

const TEST_SESSION_OWNERS_DIR_ENV = "PI_TEST_SESSION_OWNERS_DIR";

/**
 * A test run never touches the real state dir for session ownership leases:
 * bun test processes use one per-user directory in the OS temp dir. It is
 * exported when this module loads, before a test can spawn an omp process,
 * so spawned processes meet the same leases. Session ids are unique, so
 * concurrent test processes can share it, and it is reused across runs (bun
 * test fires no exit hook to remove it).
 */
function testSessionOwnersDir(): string | undefined {
	const exported = process.env[TEST_SESSION_OWNERS_DIR_ENV];
	if (exported || !isBunTestRuntime()) return exported;
	const uid = typeof process.getuid === "function" ? process.getuid() : os.userInfo().username;
	const dir = path.join(os.tmpdir(), `omp-test-session-owners-${uid}`);
	process.env[TEST_SESSION_OWNERS_DIR_ENV] = dir;
	return dir;
}
testSessionOwnersDir();

function sessionOwnersDir(): string {
	return testSessionOwnersDir() ?? getSessionOwnersDir();
}

/** Bytes read to find a header at claim time: the title slot plus a header line fit well inside. */
const SESSION_HEADER_PREFIX_BYTES = 64 * 1024;

/**
 * The session id in the header of the file at `sessionPath`, read
 * synchronously from a bounded prefix (past the title slot), or `undefined`
 * when there is no readable file or no header in that prefix.
 */
function readSessionHeaderIdSync(sessionPath: string): string | undefined {
	let prefix: string;
	try {
		const fd = fs.openSync(sessionPath, "r");
		try {
			const buf = Buffer.alloc(SESSION_HEADER_PREFIX_BYTES);
			prefix = buf.toString("utf8", 0, fs.readSync(fd, buf, 0, buf.length, 0));
		} finally {
			fs.closeSync(fd);
		}
	} catch (error) {
		if (!isEnoent(error)) throw error;
		return undefined;
	}
	if (parseTitleSlotFromContent(prefix)) prefix = prefix.slice(prefix.indexOf("\n") + 1);
	const end = prefix.indexOf("\n");
	if (end === -1) return undefined;
	try {
		const entry: unknown = JSON.parse(prefix.slice(0, end));
		if (entry && typeof entry === "object" && "type" in entry && entry.type === "session" && "id" in entry) {
			return typeof entry.id === "string" ? entry.id : undefined;
		}
	} catch {}
	return undefined;
}

/**
 * Take a session's ownership lease without waiting, or `null` while another
 * process holds it. A process writing the session holds it (see
 * `FileSessionStorage.claimSession`) until it exits; `omp gc` probes it to tell
 * whether a session is live. Keyed by the session id from the journal's
 * header, not by the file's path, so every process that reaches the journal
 * (through a symlink, a hard link, or after a move) meets the same lease. Ids
 * read from a file are untrusted: anything but a plain token is hashed so it
 * cannot name a path outside the directory. Throws when the lock cannot be
 * probed.
 */
export function tryAcquireSessionLease(sessionId: string): FileLockHandle | null {
	const name = sessionLeaseName(sessionId);
	// Linux (abstract socket) and Windows (named mutex) locks never touch the
	// filesystem; elsewhere the lock is a `flock` sidecar in that directory. The
	// sidecars are empty and never removed: unlinking a flock file can split one
	// lease between a holder of the old inode and a creator of a new one.
	if (process.platform !== "linux" && process.platform !== "win32") {
		return tryAcquireFileLock(path.join(privateSessionOwnersDir(), name));
	}
	return tryAcquireFileLock(path.join(sessionOwnersDir(), name));
}

/** Lease file stem: ids read from a file are untrusted, so anything but a plain token is hashed. */
function sessionLeaseName(sessionId: string): string {
	return /^[A-Za-z0-9_-]{1,128}$/.test(sessionId)
		? sessionId
		: `h-${Bun.hash.wyhash(sessionId).toString(16).padStart(16, "0")}`;
}

/**
 * The session owners directory, created when missing. The test-run dir sits in
 * the shared OS temp dir, where another local user could pre-create it or
 * plant a symlink: refuse anything but a directory this user owns that nobody
 * else can enter.
 */
function privateSessionOwnersDir(): string {
	const dir = sessionOwnersDir();
	fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
	if (process.platform !== "win32") {
		const st = fs.lstatSync(dir);
		const uid = process.getuid?.();
		if (!st.isDirectory() || (uid !== undefined && st.uid !== uid) || (st.mode & 0o077) !== 0) {
			throw new Error(`Session ownership lease directory ${dir} is not a private directory owned by this user`);
		}
	}
	return dir;
}

// ─── Lease holders and takeover ──────────────────────────────────────────────
//
// The OS lease above decides who writes. Next to it, the holder publishes who
// it is (`<lease>.holder.json`, display only: a crash can leave it stale, so it
// is read only while the lease is held and its pid is alive). Another process
// asks the holder to let go by writing `<lease>.takeover.json`; holders that
// subscribed (see `onSessionLeaseTakeover`) poll for it, finish their writes
// and release. Nothing is ever taken by force: a holder that does not answer
// (an older omp, a hung process) keeps the lease.
//
// A request names the claim it is for (a random id the holder publishes with
// each claim) and when it was made: one left behind by a requester that went
// away is never obeyed by a later holder, and expires after the requester's
// longest wait. Each requester withdraws only its own request.

/** How long "Open here" waits for the holder; a request older than this is abandoned. */
export const SESSION_TAKEOVER_WAIT_MS = 30_000;

/** Who holds a session's writer lease, as that process published it. */
export interface SessionLeaseHolder {
	/** This process's lease identity (random per process). */
	id: string;
	/** `ombra` for the desktop app's engine; `omp` for the terminal and CLI. */
	kind: string;
	pid: number;
	label?: string;
	/** When the lease was taken (ISO 8601). */
	since: string;
}

const leaseIdentity: { id: string; kind: string; label?: string } = { id: Snowflake.next() as string, kind: "omp" };

/** Name this process in the holder record of every lease it takes from now on. */
export function setSessionLeaseIdentity(kind: string, label?: string): void {
	leaseIdentity.kind = kind;
	if (label) leaseIdentity.label = label;
	else delete leaseIdentity.label;
}

/** This process's identity, as a holder record taken now. */
export function currentSessionLeaseHolder(): SessionLeaseHolder {
	return {
		id: leaseIdentity.id,
		kind: leaseIdentity.kind,
		pid: process.pid,
		...(leaseIdentity.label ? { label: leaseIdentity.label } : {}),
		since: new Date().toISOString(),
	};
}

function leaseSidecarPath(sessionId: string, suffix: "holder" | "takeover"): string {
	return path.join(privateSessionOwnersDir(), `${sessionLeaseName(sessionId)}.${suffix}.json`);
}

function parseLeaseHolder(value: unknown): SessionLeaseHolder | undefined {
	if (!isRecord(value)) return undefined;
	const { id, kind, pid, label, since } = value;
	if (typeof id !== "string" || typeof kind !== "string" || typeof since !== "string") return undefined;
	if (typeof pid !== "number" || !Number.isSafeInteger(pid) || pid <= 0) return undefined;
	return { id, kind, pid, since, ...(typeof label === "string" && label ? { label } : {}) };
}

function readSidecar(file: string): Record<string, unknown> | undefined {
	try {
		const value: unknown = JSON.parse(fs.readFileSync(file, "utf8"));
		return isRecord(value) ? value : undefined;
	} catch {
		return undefined;
	}
}

function writeLeaseSidecar(file: string, record: object): void {
	const temp = `${file}.${process.pid}.${Snowflake.next()}.tmp`;
	fs.writeFileSync(temp, JSON.stringify(record), { mode: 0o600 });
	fs.renameSync(temp, file);
}

/** This process's current claim of each session it holds: the id published with it, and when it was taken. */
const leaseClaims = new Map<string, { claim: string; at: number }>();

/**
 * The process holding `sessionId`'s lease, as it published itself; `undefined`
 * when the record is missing or stale (an older omp, a crashed holder).
 */
export function readSessionLeaseHolder(sessionId: string): SessionLeaseHolder | undefined {
	if (sessionLeases.has(sessionId)) return currentSessionLeaseHolder();
	const record = readSidecar(leaseSidecarPath(sessionId, "holder"));
	const holder = parseLeaseHolder(record);
	return holder && holder.pid !== process.pid && isPidAlive(holder.pid) ? holder : undefined;
}

/** Whether nobody, in this process or another, holds `sessionId`'s lease right now. */
export function isSessionLeaseFree(sessionId: string): boolean {
	if (sessionLeases.has(sessionId)) return false;
	const probe = tryAcquireSessionLease(sessionId);
	if (!probe) return false;
	probe.release();
	return true;
}

/**
 * Ask the current holder of `sessionId`'s lease to release it; `requester` is
 * shown to that holder. Returns the request's id for
 * {@link withdrawSessionLeaseTakeover}.
 */
export function requestSessionLeaseTakeover(sessionId: string, requester = currentSessionLeaseHolder()): string {
	const own = leaseClaims.get(sessionId)?.claim;
	const published = readSidecar(leaseSidecarPath(sessionId, "holder"))?.claim;
	const claim = own ?? (typeof published === "string" ? published : undefined);
	const nonce = Snowflake.next() as string;
	writeLeaseSidecar(leaseSidecarPath(sessionId, "takeover"), {
		requester,
		nonce,
		at: Date.now(),
		...(claim ? { claim } : {}),
	});
	return nonce;
}

/** Withdraw the takeover request `nonce` (granted, or given up); another requester's request stays. */
export function withdrawSessionLeaseTakeover(sessionId: string, nonce: string): void {
	const file = leaseSidecarPath(sessionId, "takeover");
	if (readSidecar(file)?.nonce !== nonce) return;
	try {
		fs.unlinkSync(file);
	} catch {}
}

type SessionLeaseTakeoverHandler = (requester: SessionLeaseHolder | undefined) => void;

const takeoverHandlers = new Map<string, SessionLeaseTakeoverHandler>();
let takeoverPoll: NodeJS.Timeout | undefined;
const TAKEOVER_POLL_MS = 250;

/**
 * Answer pending takeover requests for the leases this process holds (run
 * every {@link TAKEOVER_POLL_MS} while a handler is registered; exported so
 * tests drive it without a clock). A request for another claim, from before
 * this claim, or older than {@link SESSION_TAKEOVER_WAIT_MS} is removed unanswered.
 */
export function checkSessionLeaseTakeoversNow(): void {
	for (const [sessionId, handler] of takeoverHandlers) {
		// Only a current holder answers; a request for a lease this process gave up waits for its holder.
		const mine = leaseClaims.get(sessionId);
		if (!sessionLeases.has(sessionId) || !mine) continue;
		const file = leaseSidecarPath(sessionId, "takeover");
		const request = readSidecar(file);
		if (!request) continue;
		try {
			fs.unlinkSync(file);
		} catch {}
		const at = typeof request.at === "number" ? request.at : 0;
		const current =
			Date.now() - at <= SESSION_TAKEOVER_WAIT_MS &&
			(typeof request.claim === "string" ? request.claim === mine.claim : at >= mine.at);
		if (!current) {
			logger.warn("Dropped a stale session takeover request", { sessionId });
			continue;
		}
		takeoverHandlers.delete(sessionId);
		try {
			handler(parseLeaseHolder(request.requester));
		} catch (error) {
			logger.warn("Session lease takeover handler failed", { sessionId, error: String(error) });
		}
	}
	if (takeoverHandlers.size === 0 && takeoverPoll) {
		clearInterval(takeoverPoll);
		takeoverPoll = undefined;
	}
}

/**
 * Call `handler` once when another process (or session) asks for `sessionId`'s
 * lease while this process holds it. The handler owns the answer: it releases
 * the lease when its writes are done. Returns the unsubscribe.
 */
export function onSessionLeaseTakeover(sessionId: string, handler: SessionLeaseTakeoverHandler): () => void {
	takeoverHandlers.set(sessionId, handler);
	takeoverPoll ??= setInterval(checkSessionLeaseTakeoversNow, TAKEOVER_POLL_MS);
	takeoverPoll.unref?.();
	return () => {
		if (takeoverHandlers.get(sessionId) === handler) takeoverHandlers.delete(sessionId);
	};
}

/** Approximate size (UTF-16 units synchronously, bytes through a sink) buffered per staging-file write when streaming a body. */
const LINE_STREAM_CHUNK = 1 << 20;

/** Write `lines` to a new file at `target` in bounded chunks, never holding their full concatenation. */
function writeLinesToFileSync(target: string, lines: readonly string[]): void {
	const fd = fs.openSync(target, "w");
	try {
		let chunk = "";
		for (const line of lines) {
			chunk += line;
			if (chunk.length < LINE_STREAM_CHUNK) continue;
			fs.writeFileSync(fd, chunk);
			chunk = "";
		}
		if (chunk.length > 0) fs.writeFileSync(fd, chunk);
	} finally {
		fs.closeSync(fd);
	}
}

/**
 * Async {@link writeLinesToFileSync} through a buffered Bun file sink.
 * `target` must be a fresh path: a sink does not truncate an existing file.
 */
async function writeLinesToFile(target: string, lines: readonly string[]): Promise<void> {
	const sink = Bun.file(target).writer({ highWaterMark: LINE_STREAM_CHUNK });
	try {
		for (const line of lines) sink.write(line);
	} catch (err) {
		try {
			// Release the descriptor so the caller can discard the staging file.
			await sink.end();
		} catch {
			// The write failure is the error worth reporting.
		}
		throw err;
	}
	await sink.end();
}

export class FileSessionStorage implements SessionStorage {
	#assertExpectedSize(fpath: string, expectedSize: number | null | undefined): void {
		if (expectedSize === undefined) return;
		let actualSize: number | null;
		try {
			actualSize = fs.statSync(fpath).size;
		} catch (error) {
			if (!isEnoent(error)) throw error;
			actualSize = null;
		}
		if (actualSize !== expectedSize) {
			throw new SessionWriteConflictError(fpath, expectedSize, actualSize);
		}
	}

	#publishLockPath(fpath: string): string {
		return path.join(path.dirname(fpath), `.${path.basename(fpath)}.lock`);
	}

	/**
	 * Run `task` (the freshness check through the final rename) while holding
	 * the cross-process publish lock for `fpath`. The OS gate is acquired
	 * first and released last, so the lockfile claim below only ever runs
	 * while this process provably owns the name.
	 */
	#withPublishLock(fpath: string, task: () => void): void {
		const lockPath = this.#publishLockPath(fpath);
		// The lock lives beside the session file: the directory may not exist
		// yet when the first publish creates it (writeTextSync creates it for
		// the temp file, but the lock claim runs first). Match that behavior
		// so a first publish to a new directory does not fail with ENOENT.
		this.ensureDirSync(path.dirname(lockPath));
		const osGate = this.#acquireOsPublishLock(fpath, this.#osGatePath(lockPath));
		try {
			this.#acquirePublishLock(fpath, lockPath);
			try {
				task();
			} finally {
				this.#releasePublishLock(fpath, lockPath);
			}
		} finally {
			osGate.release();
		}
	}

	#releasePublishLock(fpath: string, lockPath: string): void {
		try {
			fs.unlinkSync(lockPath);
		} catch (err) {
			if (!isEnoent(err)) {
				logger.warn("Failed to remove session publish lock", { sessionFile: fpath, lockPath });
			}
		}
	}

	/**
	 * Run `append` under the OS gate, claiming the lockfile only when a
	 * previous-binary publisher may overlap it. Current publishers hold the
	 * gate across their freshness check and rename, so among current writers
	 * the gate alone excludes them and an append touches nothing beside the
	 * session. A previous binary takes only the lockfile, so its claim stays
	 * observable: an append that finds the lockfile waits for it exactly as
	 * before, and one that saw it appear (or saw the path renamed away from
	 * the descriptor it wrote through) waits for that publisher to finish and
	 * re-appends to whatever file it published when the line did not survive.
	 * That reproduces the serial order "their publish, then our append" the
	 * lockfile used to enforce, and fails closed when the holder outlives the
	 * bounded wait.
	 *
	 * `lockPath` and `gatePath` are the writer's precomputed
	 * {@link #publishLockPath} and {@link #osGatePath}. The held descriptor
	 * already lives in the session directory, so the directory is recreated
	 * only when acquiring the gate fails (the `flock` sidecar needs it).
	 */
	#withAppendLock(fpath: string, lockPath: string, gatePath: string, append: () => void, landed: () => boolean): void {
		let osGate: NativeFileLock;
		try {
			osGate = this.#acquireOsPublishLock(fpath, gatePath);
		} catch (err) {
			if (err instanceof SessionLockError) throw err;
			this.ensureDirSync(path.dirname(lockPath));
			osGate = this.#acquireOsPublishLock(fpath, gatePath);
		}
		try {
			let appended = false;
			if (!fs.existsSync(lockPath)) {
				append();
				appended = true;
				// Lockfile before identity: a previous-binary publisher whose
				// freshness check preceded our write holds the lockfile through its
				// rename, so it is either still visible here or has already moved
				// the path off our descriptor.
				if (!fs.existsSync(lockPath) && landed()) return;
			}
			this.#acquirePublishLock(fpath, lockPath);
			try {
				if (!appended || !landed()) append();
			} finally {
				this.#releasePublishLock(fpath, lockPath);
			}
		} finally {
			osGate.release();
		}
	}

	/**
	 * Claim the process-owned gate at `gatePath` ({@link #osGatePath}) for
	 * `fpath`, failing closed after the same bounded wait the lockfile claim
	 * uses. The native handle keeps ownership, never the file content, so
	 * suspension and SIGKILL cannot strand it as stealable.
	 */
	#acquireOsPublishLock(fpath: string, gatePath: string): NativeFileLock {
		const deadline = Date.now() + SESSION_PUBLISH_LOCK_WAIT_MS;
		for (;;) {
			const gate = NativeFileLock.tryAcquire(gatePath);
			if (gate.acquired) return gate;
			gate.release();
			if (Date.now() >= deadline) {
				throw new SessionLockError(fpath, "another writer holds the publish lock");
			}
			sleepSyncMs(SESSION_PUBLISH_LOCK_POLL_MS);
		}
	}

	/**
	 * Sidecar carrying the OS gate. It lives beside the lockfile (same trust
	 * domain). Platforms backed by `flock(2)` require this path to remain
	 * persistent: unlinking it after release can race a successor that already
	 * opened the old inode, allowing a third process to lock a new inode at the
	 * same path concurrently. Only handle ownership matters, so a
	 * crash-orphaned sidecar is inert and the next acquire simply reopens it.
	 */
	#osGatePath(lockPath: string): string {
		return `${lockPath}.os`;
	}

	#acquirePublishLock(fpath: string, lockPath: string): void {
		const deadline = Date.now() + SESSION_PUBLISH_LOCK_WAIT_MS;
		for (;;) {
			if (this.#createPublishLock(lockPath)) return;
			if (Date.now() >= deadline) {
				throw new SessionLockError(fpath, "another writer holds the publish lock");
			}
			sleepSyncMs(SESSION_PUBLISH_LOCK_POLL_MS);
		}
	}

	#createPublishLock(lockPath: string): boolean {
		if (this.#tryCreatePublishLock(lockPath)) return true;
		if (!this.#stealStalePublishLock(lockPath)) return false;
		return this.#tryCreatePublishLock(lockPath);
	}

	/**
	 * Claim the lock name and record its holder. Returns false when another
	 * holder already owns the name. A failure to record the holder removes the
	 * file it just created, so no caller meets a contentless lock it can
	 * neither attribute to a live pid nor safely steal.
	 */
	#tryCreatePublishLock(lockPath: string): boolean {
		let fd: number;
		try {
			fd = fs.openSync(lockPath, "wx", 0o600);
		} catch (err) {
			if (!hasFsCode(err, "EEXIST")) throw toError(err);
			return false;
		}
		const record = `${process.pid}:${Date.now()}\n`;
		try {
			fs.writeFileSync(fd, record);
		} catch (err) {
			try {
				fs.closeSync(fd);
			} catch {
				// Descriptor unusable after the failed write; the unlink matters.
			}
			try {
				fs.unlinkSync(lockPath);
			} catch {
				// A concurrent steal already removed it.
			}
			throw toError(err);
		}
		// Verify the record survived: a concurrent stale-lock steal may have
		// removed our file between create and write (a POSIX fd write succeeds
		// on the unlinked inode), in which case we hold nothing. Retry instead
		// of entering the region unexclusively (hV-oE). The path still naming
		// our inode proves the record is there (holders only ever create and
		// unlink lock files), and comparing identities costs two stats instead
		// of a full open/read/close. The descriptor stays open until after the
		// comparison so the inode cannot be freed and its number reused by a
		// successor's file; bigint keeps 64-bit Windows file IDs exact.
		try {
			const held = fs.fstatSync(fd, { bigint: true });
			const named = fs.statSync(lockPath, { bigint: true });
			return held.ino === named.ino && held.dev === named.dev;
		} catch {
			// Removed under us: hold nothing, retry.
			return false;
		} finally {
			try {
				fs.closeSync(fd);
			} catch {
				// Ignore close errors; the lock content is already written.
			}
		}
	}

	/**
	 * Remove the lock only when its holder is verifiably dead. Returns whether
	 * the caller should retry acquisition: true when the lock vanished (ours
	 * to take) or was stolen, false when a live holder owns it.
	 */
	#stealStalePublishLock(lockPath: string): boolean {
		let content: string;
		try {
			content = fs.readFileSync(lockPath, "utf8");
		} catch (err) {
			return !!isEnoent(err);
		}
		const pid = publishLockPid(content);
		if (pid !== undefined) {
			if (isPidAlive(pid)) return false;
		} else if (!this.#isOrphanedPublishLock(lockPath)) {
			// Contentless or malformed with a fresh mtime: a live acquirer may
			// still be between create and record. Only a file older than any
			// live acquisition can be a crash orphan (hV-oE).
			return false;
		}
		// Re-read immediately before removal: a concurrent recovery may have
		// replaced the dead holder's file with a live lock since the first
		// read. Remove only what was verified (rJDh).
		try {
			if (fs.readFileSync(lockPath, "utf8") !== content) return false;
		} catch (err) {
			return !!isEnoent(err);
		}
		try {
			fs.unlinkSync(lockPath);
			return true;
		} catch (err) {
			return !!isEnoent(err);
		}
	}

	/**
	 * Whether a contentless or malformed lock file is old enough that no live
	 * acquirer could own it: holders write their record microseconds after
	 * create and release after a check-and-rename critical section, so
	 * anything older than the full acquisition wait budget is a crash orphan.
	 */
	#isOrphanedPublishLock(lockPath: string): boolean {
		let mtimeMs: number;
		try {
			mtimeMs = fs.statSync(lockPath).mtimeMs;
		} catch {
			// Vanished mid-check; the steal path re-verifies before removal.
			return true;
		}
		return Date.now() - mtimeMs > SESSION_PUBLISH_LOCK_WAIT_MS;
	}

	ensureDirSync(dir: string): void {
		if (!fs.existsSync(dir)) {
			fs.mkdirSync(dir, { recursive: true });
		}
	}

	existsSync(path: string): boolean {
		return fs.existsSync(path);
	}

	writeTextSync(fpath: string, content: string, options?: SessionStorageWriteOptions): void {
		this.#publishSync(fpath, tempPath => fs.writeFileSync(tempPath, content), options);
	}

	/**
	 * Streams `lines` into the staging file in bounded chunks. A subclass or
	 * spy that intercepts {@link writeTextSync} still sees every full rewrite:
	 * the lines are then joined and routed through it.
	 */
	writeLinesSync(fpath: string, lines: readonly string[], options?: SessionStorageWriteOptions): void {
		if (this.writeTextSync !== FileSessionStorage.prototype.writeTextSync) {
			this.writeTextSync(fpath, lines.join(""), options);
			return;
		}
		this.#publishSync(fpath, tempPath => writeLinesToFileSync(tempPath, lines), options);
	}

	/** Stage a body via `stage` at a fresh temp path beside `fpath`, then check freshness and rename it in. */
	#publishSync(
		fpath: string,
		stage: (tempPath: string) => void,
		options: SessionStorageWriteOptions | undefined,
	): void {
		const dir = path.dirname(fpath);
		this.ensureDirSync(dir);
		const tempPath = path.join(dir, `.${path.basename(fpath)}.${Snowflake.next()}.tmp`);
		try {
			stage(tempPath);
		} catch (err) {
			this.#discardTemp(tempPath, fpath);
			throw toError(err);
		}
		// The freshness check through the final rename runs under the
		// cross-process publish lock: no cooperating writer can slip an
		// append between the size check and the rename.
		try {
			this.#withPublishLock(fpath, () => {
				this.#assertExpectedSize(fpath, options?.expectedSize);
				try {
					this.renameSync(tempPath, fpath);
				} catch (err) {
					if (!hasFsCode(err, "EPERM")) throw toError(err);
					this.#replaceSessionFileAfterEpermSync(tempPath, fpath, err);
				}
			});
		} catch (err) {
			this.#discardTemp(tempPath, fpath);
			throw toError(err);
		}
	}

	async updateSessionTitle(fpath: string, update: SessionTitleUpdate): Promise<void> {
		const fd = fs.openSync(fpath, "r+");
		try {
			const buf = Buffer.from(serializeTitleSlot(update), "utf-8");
			let offset = 0;
			while (offset < buf.length) {
				const written = fs.writeSync(fd, buf, offset, buf.length - offset, offset);
				if (written === 0) {
					throw new Error("Short write");
				}
				offset += written;
			}
		} catch (err) {
			throw toError(err);
		} finally {
			fs.closeSync(fd);
		}
	}

	statSync(path: string): SessionStorageStat {
		const stats = fs.statSync(path);
		return { size: stats.size, mtimeMs: stats.mtimeMs, mtime: stats.mtime };
	}

	listFilesSync(dir: string, pattern: string): string[] {
		try {
			return Array.from(new Bun.Glob(pattern).scanSync(dir)).map(name => path.join(dir, name));
		} catch {
			return [];
		}
	}

	async exists(path: string): Promise<boolean> {
		try {
			await fs.promises.access(path);
			return true;
		} catch (err) {
			if (isEnoent(err)) return false;
			throw err;
		}
	}

	readText(path: string): Promise<string> {
		return Bun.file(path).text();
	}

	readTextSync(path: string): string {
		return fs.readFileSync(path, "utf8");
	}

	async readTextSlices(path: string, prefixBytes: number, suffixBytes: number): Promise<[string, string]> {
		return peekFileEnds(path, prefixBytes, suffixBytes, (head, tail) => [
			utf8Decoder.decode(head),
			utf8Decoder.decode(tail),
		]);
	}

	async hasAssistantTurn(path: string): Promise<boolean> {
		const fileHandle = await fsp.open(path, "r");
		try {
			for await (const line of fileHandle.readLines()) {
				if (isAssistantMessageLine(line)) return true;
			}
			return false;
		} finally {
			await fileHandle.close();
		}
	}

	async writeText(path: string, content: string): Promise<void> {
		await Bun.write(path, content, { createPath: true });
	}

	async writeTextAtomic(fpath: string, content: string, options?: WriteTextAtomicOptions): Promise<void> {
		await this.#publishAtomic(fpath, tempPath => fs.promises.writeFile(tempPath, content), options);
	}

	/**
	 * Streams `lines` into the staging file through a buffered file sink. A
	 * subclass or spy that intercepts {@link writeTextAtomic} still sees every
	 * full rewrite: the lines are then joined and routed through it.
	 */
	async writeLinesAtomic(fpath: string, lines: readonly string[], options?: WriteTextAtomicOptions): Promise<void> {
		if (this.writeTextAtomic !== FileSessionStorage.prototype.writeTextAtomic) {
			return this.writeTextAtomic(fpath, lines.join(""), options);
		}
		await this.#publishAtomic(fpath, tempPath => writeLinesToFile(tempPath, lines), options);
	}

	/** Async {@link #publishSync}: stages via `stage`, then guard-checks and publishes without yielding. */
	async #publishAtomic(
		fpath: string,
		stage: (tempPath: string) => Promise<void>,
		options: WriteTextAtomicOptions | undefined,
	): Promise<void> {
		const dir = path.resolve(fpath, "..");
		const tempPath = path.join(dir, `.${path.basename(fpath)}.${Snowflake.next()}.tmp`);
		await fs.promises.mkdir(dir, { recursive: true });
		try {
			await stage(tempPath);
		} catch (err) {
			this.#discardTemp(tempPath, fpath);
			throw toError(err);
		}
		// Guard-check + rename MUST NOT be separated by an await. A concurrent
		// synchronous rewrite (flushSync -> #rewriteSynchronously) can otherwise
		// publish a fresh body between the check and the rename, and this stale
		// staged body would overwrite it. Sync rename closes that window.
		if (options?.commitGuard && !options.commitGuard()) {
			this.#discardTemp(tempPath, fpath);
			return;
		}
		try {
			// The publish lock spans the freshness check through the rename (and
			// its EPERM fallback): a cooperating appender or rewrite cannot
			// interleave, and appenders re-open a replaced path before writing.
			this.#withPublishLock(fpath, () => {
				this.#assertExpectedSize(fpath, options?.expectedSize);
				try {
					this.renameSync(tempPath, fpath);
					return;
				} catch (err) {
					if (!hasFsCode(err, "EPERM")) throw toError(err);
					this.#replaceSessionFileAfterEpermSync(tempPath, fpath, err, options?.commitGuard);
				}
			});
		} catch (err) {
			this.#discardTemp(tempPath, fpath);
			throw toError(err);
		}
	}

	/**
	 * Sync rename hook. Split from `rename` so `writeTextAtomic` can perform its
	 * guard-then-publish step without a yield, and so tests can inject
	 * Windows-style EPERM at the sync layer used by the atomic path.
	 */
	renameSync(source: string, target: string): void {
		fs.renameSync(source, target);
	}

	#discardTemp(tempPath: string, targetPath: string): void {
		try {
			fs.unlinkSync(tempPath);
		} catch (err) {
			if (!isEnoent(err)) {
				logger.warn("Failed to remove session rewrite temp file", {
					sessionFile: targetPath,
					tempPath,
					error: toError(err).message,
				});
			}
		}
	}

	#replaceSessionFileAfterEpermSync(
		tempPath: string,
		targetPath: string,
		renameError: unknown,
		commitGuard?: () => boolean,
	): void {
		const dir = path.resolve(targetPath, "..");
		const backupPath = path.join(dir, `${path.basename(targetPath)}.${Snowflake.next()}.bak`);
		try {
			this.renameSync(targetPath, backupPath);
		} catch (moveAsideError) {
			if (isEnoent(moveAsideError)) {
				if (commitGuard && !commitGuard()) {
					this.#discardTemp(tempPath, targetPath);
					return;
				}
				this.renameSync(tempPath, targetPath);
				return;
			}
			throw toError(renameError);
		}
		if (commitGuard && !commitGuard()) {
			// A concurrent synchronous rewrite published a fresh body between the
			// move-aside and this point. Restore the moved-aside file so we do
			// not overwrite it with our staged (stale) body, and drop the temp
			// so `writeTextAtomic`'s "discard on abandon" contract holds.
			try {
				this.renameSync(backupPath, targetPath);
			} catch (restoreErr) {
				logger.warn("Failed to restore backup after commitGuard rejection", {
					sessionFile: targetPath,
					backupPath,
					error: toError(restoreErr).message,
				});
			}
			this.#discardTemp(tempPath, targetPath);
			return;
		}
		try {
			this.renameSync(tempPath, targetPath);
		} catch (replaceError) {
			try {
				this.renameSync(backupPath, targetPath);
			} catch (rollbackErr) {
				const rollbackError = toError(rollbackErr);
				throw new Error(
					`Failed to replace session file after EPERM (original: ${toError(renameError).message}; retry: ${
						toError(replaceError).message
					}; rollback: ${rollbackError.message})`,
					{ cause: toError(renameError) },
				);
			}
			throw toError(replaceError);
		}
		try {
			fs.unlinkSync(backupPath);
		} catch (err) {
			if (!isEnoent(err)) {
				logger.warn("Failed to remove session rewrite backup", {
					sessionFile: targetPath,
					backupPath,
					error: toError(err).message,
				});
			}
		}
	}

	async rename(path: string, nextPath: string): Promise<void> {
		try {
			await fs.promises.rename(path, nextPath);
		} catch (err) {
			throw toError(err);
		}
	}

	unlink(path: string): Promise<void> {
		return fs.promises.unlink(path);
	}

	drain(): Promise<void> {
		// File writes complete synchronously in-body via fs.writeFileSync /
		// fs.renameSync, so there is no queued work to await.
		return Promise.resolve();
	}

	openWriter(fpath: string, options?: { flags?: "a" | "w"; onError?: (err: Error) => void }): SessionStorageWriter {
		const lockPath = this.#publishLockPath(fpath);
		const gatePath = this.#osGatePath(lockPath);
		return new FileSessionStorageWriter(fpath, {
			...options,
			publishLock: (append, landed) => this.#withAppendLock(fpath, lockPath, gatePath, append, landed),
		});
	}

	/** Run a synchronous session mutation under its cross-process lock. */
	withSessionFileLockSync<T>(sessionPath: string, operation: () => T): T {
		return withFileLockSync(sessionPath, operation);
	}

	/**
	 * The lease is an OS lock (abstract socket, named mutex, or `flock` sidecar
	 * under ~/.omp/run/session-owners), so the kernel drops a dead owner's claim.
	 * Lock-provider failures propagate: local-only exclusion cannot protect a
	 * journal from another process and must never authorize a write. The holder
	 * record next to it only names this process for display.
	 */
	claimSession(sessionId: string, sessionPath: string): (() => void) | null {
		const onDisk = readSessionHeaderIdSync(sessionPath);
		if (onDisk !== undefined && onDisk !== sessionId) return null;
		if (sessionLeases.has(sessionId)) return null;
		const lease = tryAcquireSessionLease(sessionId);
		if (!lease) return null;
		sessionLeases.set(sessionId, lease);
		// Takeover requests name this claim; one made for an earlier holder is never obeyed here.
		const claim = { claim: Snowflake.next() as string, at: Date.now() };
		leaseClaims.set(sessionId, claim);
		let holderFile: string | undefined;
		try {
			holderFile = leaseSidecarPath(sessionId, "holder");
			writeLeaseSidecar(holderFile, { ...currentSessionLeaseHolder(), claim: claim.claim });
		} catch (error) {
			logger.warn("Could not publish the session lease holder", { sessionId, error: String(error) });
		}
		let released = false;
		return () => {
			if (released) return;
			released = true;
			// Withdrawn while still held, so it never removes the next holder's record.
			if (holderFile) {
				try {
					fs.unlinkSync(holderFile);
				} catch {}
			}
			if (leaseClaims.get(sessionId) === claim) leaseClaims.delete(sessionId);
			if (sessionLeases.get(sessionId) === lease) sessionLeases.delete(sessionId);
			lease.release();
		};
	}

	sessionLeaseHolder(sessionId: string): SessionLeaseHolder | undefined {
		return readSessionLeaseHolder(sessionId);
	}

	onSessionLeaseTakeover(sessionId: string, handler: (requester: SessionLeaseHolder | undefined) => void): () => void {
		return onSessionLeaseTakeover(sessionId, handler);
	}

	/** Deletion participates in the same lifetime ownership protocol as writers. */
	#acquireDeletionLease(sessionPath: string): () => void {
		if (!sessionPath.endsWith(".jsonl")) throw new Error(`Not a session file: ${sessionPath}`);
		const id = readSessionHeaderIdSync(sessionPath);
		if (!id) {
			fs.statSync(sessionPath); // Preserve ENOENT for idempotent manager deletion.
			throw new Error(`Cannot establish session ownership for deletion: ${sessionPath}`);
		}
		const release = this.claimSession(id, sessionPath);
		if (!release) throw new Error(`Session is busy in another live writer; nothing was deleted: ${sessionPath}`);
		try {
			if (readSessionHeaderIdSync(sessionPath) !== id)
				throw new Error(`Session changed while acquiring deletion ownership: ${sessionPath}`);
			return release;
		} catch (error) {
			release();
			throw error;
		}
	}

	/** A parent deletion also removes nested subagent journals in its artifacts. */
	#acquireDeletionTree(sessionPath: string): () => void {
		const releases = [this.#acquireDeletionLease(sessionPath)];
		const releaseAll = () => { for (const release of releases.reverse()) release(); };
		try {
			const artifacts = sessionPath.slice(0, -6);
			let directories: string[] = [];
			try {
				if (fs.lstatSync(artifacts).isDirectory()) directories = [artifacts];
			} catch (error) {
				if (!isEnoent(error)) throw error;
			}
			while (directories.length) {
				const directory = directories.pop()!;
				for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
					const file = path.join(directory, entry.name);
					if (entry.isDirectory()) directories.push(file);
					else if (entry.isFile() && entry.name.endsWith(".jsonl") && readSessionHeaderIdSync(file))
						releases.push(this.#acquireDeletionLease(file));
				}
			}
			return releaseAll;
		} catch (error) {
			releaseAll();
			throw error;
		}
	}

	/**
	 * Conditionally delete under the same cross-process lock used by the first
	 * durable append to a draft-only session.
	 */
	async deleteSessionWithArtifactsIf(sessionPath: string, shouldDelete: (content: string) => boolean): Promise<boolean> {
		const release = this.#acquireDeletionTree(sessionPath);
		try {
			return this.withSessionFileLockSync(sessionPath, () => {
				const content = fs.readFileSync(sessionPath, "utf-8");
				if (!shouldDelete(content)) return false;

				fs.unlinkSync(sessionPath);
				const artifactsDir = sessionPath.slice(0, -6);
				try {
					fs.rmSync(artifactsDir, { recursive: true, force: true });
				} catch (err) {
					const error = toError(err);
					throw new Error(
						`Session file deleted but failed to remove artifacts directory ${artifactsDir}: ${error.message}`,
						{ cause: error },
					);
				}
				return true;
			});
		} finally {
			release();
		}
	}

	/**
	 * Delete a session file and its artifacts directory.
	 * Artifacts are stored in a sibling directory with the same name minus .jsonl extension.
	 */
	async deleteSessionWithArtifacts(sessionPath: string): Promise<void> {
		const release = this.#acquireDeletionTree(sessionPath);
		try {
			// Hold ownership until the journal and all associated artifacts are gone.
			await this.unlink(sessionPath);

			// Compute artifacts directory: /path/to/session.jsonl -> /path/to/session
			const artifactsDir = sessionPath.slice(0, -6);

			// Delete artifacts directory if it exists. Missing directories are fine, but
			// surface real cleanup failures because the session file is already gone.
			try {
				await fsp.rm(artifactsDir, { recursive: true, force: true });
			} catch (err) {
				const error = toError(err);
				throw new Error(
					`Session file deleted but failed to remove artifacts directory ${artifactsDir}: ${error.message}`,
					{ cause: error },
				);
			}

			// Remove EPERM-rewrite leftovers (`<name>.jsonl.<snowflake>.bak`): the
			// picker scan would otherwise resurrect the deleted session from the
			// newest stale backup (#11499). Best-effort — a locked file warns
			// instead of failing the delete the user asked for.
			const base = path.basename(sessionPath);
			for (const bak of this.listFilesSync(path.dirname(sessionPath), "*.bak")) {
				if (!path.basename(bak).startsWith(`${base}.`)) continue;
				try {
					await fsp.unlink(bak);
				} catch (err) {
					logger.warn("Failed to remove stale session backup during delete", {
						path: bak,
						error: toError(err).message,
					});
				}
			}
		} finally {
			release();
		}
	}
}

function matchesPattern(name: string, pattern: string): boolean {
	if (pattern === "*") return true;
	if (pattern.startsWith("*.")) {
		return name.endsWith(pattern.slice(1));
	}
	return name === pattern;
}

/**
 * Name of `key` when it sits directly inside `resolvedDir` (a `path.resolve`d
 * directory), else `undefined`. Key-indexed storages keep whatever spelling
 * callers wrote, so both sides are resolved: on Windows `/sessions/x` and the
 * `path.join`-built `\sessions\x\…` keys must list as the same directory.
 */
export function directChildKeyName(resolvedDir: string, key: string): string | undefined {
	const name = path.basename(key);
	if (!name || name.includes("/") || name.includes("\\")) return undefined;
	return path.resolve(path.dirname(key)) === resolvedDir ? name : undefined;
}

class MemorySessionStorageWriter implements SessionStorageWriter {
	#storage: MemorySessionStorage;
	#path: string;
	#closed = false;
	#error: Error | undefined;
	#onError: ((err: Error) => void) | undefined;

	constructor(
		storage: MemorySessionStorage,
		path: string,
		options?: { flags?: "a" | "w"; onError?: (err: Error) => void },
	) {
		this.#storage = storage;
		this.#path = path;
		this.#onError = options?.onError;
		if ((options?.flags ?? "a") === "w") {
			this.#storage.writeTextSync(path, "");
		}
	}

	#recordError(err: unknown): Error {
		const error = toError(err);
		if (!this.#error) this.#error = error;
		this.#onError?.(error);
		return error;
	}

	appendSync(line: string): void {
		if (this.#closed) throw new Error("Writer closed");
		if (this.#error) throw this.#error;
		try {
			// O(1) append — push onto the path's indexed in-memory entry.
			this.#storage.appendSync(this.#path, line);
		} catch (err) {
			throw this.#recordError(err);
		}
	}

	async append(line: string): Promise<void> {
		this.appendSync(line);
	}

	async flush(): Promise<void> {
		if (this.#error) throw this.#error;
	}

	flushSync(): void {
		if (this.#error) throw this.#error;
	}

	isOpen(): boolean {
		return !this.#closed;
	}

	async close(): Promise<void> {
		if (this.#closed) return;
		this.#closed = true;
	}

	getError(): Error | undefined {
		return this.#error;
	}
}

interface MemoryFileEntry {
	chunks: string[];
	cumulativeBytes: number[];
	size: number;
	mtimeMs: number;
}

function createMemoryFileEntry(content: string, mtimeMs: number): MemoryFileEntry {
	const size = Buffer.byteLength(content, "utf-8");
	return {
		chunks: size === 0 ? [] : [content],
		cumulativeBytes: size === 0 ? [] : [size],
		size,
		mtimeMs,
	};
}

function appendMemoryChunk(entry: MemoryFileEntry, chunk: string): void {
	const chunkSize = Buffer.byteLength(chunk, "utf-8");
	if (chunkSize === 0) return;
	entry.size += chunkSize;
	entry.chunks.push(chunk);
	entry.cumulativeBytes.push(entry.size);
}

function normalizeByteLimit(maxBytes: number, size: number): number {
	if (!(maxBytes > 0) || size === 0) return 0;
	return Math.min(Math.trunc(maxBytes), size);
}

function lowerBound(values: readonly number[], target: number): number {
	let lo = 0;
	let hi = values.length;
	while (lo < hi) {
		const mid = (lo + hi) >>> 1;
		if (values[mid] < target) {
			lo = mid + 1;
		} else {
			hi = mid;
		}
	}
	return lo;
}

function upperBound(values: readonly number[], target: number): number {
	let lo = 0;
	let hi = values.length;
	while (lo < hi) {
		const mid = (lo + hi) >>> 1;
		if (values[mid] <= target) {
			lo = mid + 1;
		} else {
			hi = mid;
		}
	}
	return lo;
}

function joinChunkRange(chunks: readonly string[], start: number, end: number): string {
	const count = end - start;
	if (count <= 0) return "";
	if (count === 1) return chunks[start] ?? "";

	let content = "";
	for (let i = start; i < end; i++) {
		content += chunks[i];
	}
	return content;
}

function decodeChunkByteRange(chunk: string, startByte: number, endByte: number, chunkSize: number): string {
	if (startByte >= endByte) return "";
	if (startByte === 0 && endByte === chunkSize) return chunk;
	if (chunk.length === chunkSize) return chunk.slice(startByte, endByte);
	const bytes = Buffer.from(chunk, "utf-8");
	return utf8Decoder.decode(bytes.subarray(startByte, endByte));
}

function materializeMemoryEntry(entry: MemoryFileEntry): string {
	const { chunks } = entry;
	if (chunks.length === 0) return "";
	if (chunks.length === 1) return chunks[0];

	const content = chunks.join("");
	entry.chunks = [content];
	entry.cumulativeBytes = [entry.size];
	return content;
}

function sliceChunksHead(entry: MemoryFileEntry, maxBytes: number): string {
	const limit = normalizeByteLimit(maxBytes, entry.size);
	if (limit === 0) return "";
	if (limit >= entry.size) return materializeMemoryEntry(entry);

	const boundaryIndex = lowerBound(entry.cumulativeBytes, limit);
	const chunkStart = boundaryIndex === 0 ? 0 : entry.cumulativeBytes[boundaryIndex - 1];
	const chunkEnd = entry.cumulativeBytes[boundaryIndex];
	if (chunkEnd === limit) return joinChunkRange(entry.chunks, 0, boundaryIndex + 1);

	const chunk = entry.chunks[boundaryIndex];
	const chunkPrefix = decodeChunkByteRange(chunk, 0, limit - chunkStart, chunkEnd - chunkStart);
	return joinChunkRange(entry.chunks, 0, boundaryIndex) + chunkPrefix;
}

function sliceChunksTail(entry: MemoryFileEntry, maxBytes: number): string {
	const limit = normalizeByteLimit(maxBytes, entry.size);
	if (limit === 0) return "";
	if (limit >= entry.size) return materializeMemoryEntry(entry);

	const startByte = entry.size - limit;
	const boundaryIndex = upperBound(entry.cumulativeBytes, startByte);
	const chunkStart = boundaryIndex === 0 ? 0 : entry.cumulativeBytes[boundaryIndex - 1];
	const chunkEnd = entry.cumulativeBytes[boundaryIndex];
	const chunkOffset = startByte - chunkStart;
	if (chunkOffset === 0) return joinChunkRange(entry.chunks, boundaryIndex, entry.chunks.length);

	const chunk = entry.chunks[boundaryIndex];
	const chunkSuffix = decodeChunkByteRange(chunk, chunkOffset, chunkEnd - chunkStart, chunkEnd - chunkStart);
	return chunkSuffix + joinChunkRange(entry.chunks, boundaryIndex + 1, entry.chunks.length);
}

/** A missing in-memory file, coded `ENOENT` like the file backend so callers' `isEnoent` checks agree. */
function memoryFileNotFound(path: string): FsError {
	return Object.assign(new Error(`File not found: ${path}`), { code: "ENOENT" });
}

export class MemorySessionStorage implements SessionStorage {
	// Each path keeps appended string chunks plus cumulative UTF-8 byte offsets.
	// Full reads materialize the chunks into one string chunk, so repeated reads
	// do not re-join stale history. Later appends still stay O(1) by pushing
	// after that materialized chunk. Prefix/suffix reads binary-search byte
	// offsets and join only the requested window.
	#files = new Map<string, MemoryFileEntry>();

	#requireEntry(path: string): MemoryFileEntry {
		const entry = this.#files.get(path);
		if (!entry) throw memoryFileNotFound(path);
		return entry;
	}

	ensureDirSync(_dir: string): void {
		// No-op for in-memory storage.
	}

	existsSync(path: string): boolean {
		return this.#files.has(path);
	}

	writeTextSync(path: string, content: string, options?: SessionStorageWriteOptions): void {
		const actualSize = this.#files.get(path)?.size ?? null;
		if (options?.expectedSize !== undefined && actualSize !== options.expectedSize) {
			throw new SessionWriteConflictError(path, options.expectedSize, actualSize);
		}
		this.#files.set(path, createMemoryFileEntry(content, Date.now()));
	}

	async updateSessionTitle(path: string, update: SessionTitleUpdate): Promise<void> {
		const entry = this.#requireEntry(path);
		this.#files.set(
			path,
			createMemoryFileEntry(overlayTitleSlotContent(materializeMemoryEntry(entry), update), Date.now()),
		);
	}

	/**
	 * Internal O(1) append used by {@link MemorySessionStorageWriter}. Lazily
	 * creates the entry. External callers should go through `openWriter()`
	 * rather than touching the mirror directly.
	 */
	appendSync(path: string, chunk: string): void {
		const mtimeMs = Date.now();
		let entry = this.#files.get(path);
		if (!entry) {
			entry = createMemoryFileEntry("", mtimeMs);
			this.#files.set(path, entry);
		}
		appendMemoryChunk(entry, chunk);
		entry.mtimeMs = mtimeMs;
	}

	statSync(path: string): SessionStorageStat {
		const entry = this.#requireEntry(path);
		return {
			size: entry.size,
			mtimeMs: entry.mtimeMs,
			mtime: new Date(entry.mtimeMs),
		};
	}

	listFilesSync(dir: string, pattern: string): string[] {
		const resolvedDir = path.resolve(dir);
		const files: string[] = [];
		for (const key of this.#files.keys()) {
			const name = directChildKeyName(resolvedDir, key);
			if (name === undefined || !matchesPattern(name, pattern)) continue;
			files.push(key);
		}
		return files;
	}

	exists(path: string): Promise<boolean> {
		return Promise.resolve(this.existsSync(path));
	}

	readText(path: string): Promise<string> {
		const entry = this.#files.get(path);
		if (!entry) return Promise.reject(memoryFileNotFound(path));
		return Promise.resolve(materializeMemoryEntry(entry));
	}

	readTextSync(path: string): string {
		const entry = this.#files.get(path);
		if (!entry) throw memoryFileNotFound(path);
		return materializeMemoryEntry(entry);
	}

	readTextSlices(path: string, prefixBytes: number, suffixBytes: number): Promise<[string, string]> {
		const entry = this.#files.get(path);
		if (!entry) return Promise.reject(memoryFileNotFound(path));
		return Promise.resolve([sliceChunksHead(entry, prefixBytes), sliceChunksTail(entry, suffixBytes)]);
	}

	async hasAssistantTurn(path: string): Promise<boolean> {
		const entry = this.#files.get(path);
		if (!entry) throw memoryFileNotFound(path);
		for (const line of materializeMemoryEntry(entry).split("\n")) {
			if (isAssistantMessageLine(line)) return true;
		}
		return false;
	}

	writeText(path: string, content: string): Promise<void> {
		this.writeTextSync(path, content);
		return Promise.resolve();
	}

	writeTextAtomic(path: string, content: string, options?: WriteTextAtomicOptions): Promise<void> {
		if (options?.commitGuard && !options.commitGuard()) return Promise.resolve();
		this.writeTextSync(path, content, { expectedSize: options?.expectedSize });
		return Promise.resolve();
	}

	rename(path: string, nextPath: string): Promise<void> {
		const entry = this.#files.get(path);
		if (!entry) return Promise.reject(memoryFileNotFound(path));
		this.#files.set(nextPath, entry);
		this.#files.delete(path);
		return Promise.resolve();
	}

	unlink(path: string): Promise<void> {
		this.#files.delete(path);
		return Promise.resolve();
	}
	deleteSessionWithArtifacts(_sessionPath: string): Promise<void> {
		return Promise.resolve();
	}

	drain(): Promise<void> {
		return Promise.resolve();
	}

	openWriter(path: string, options?: { flags?: "a" | "w"; onError?: (err: Error) => void }): SessionStorageWriter {
		return new MemorySessionStorageWriter(this, path, options);
	}
}
