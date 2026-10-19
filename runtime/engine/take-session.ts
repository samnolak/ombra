import { SessionLeaseHeldError } from "./coding-agent/src/session/session-lease-error";
import { readSessionHeaderId } from "./coding-agent/src/session/session-loader";
import {
	isSessionLeaseFree,
	readSessionLeaseHolder,
	requestSessionLeaseTakeover,
	SESSION_TAKEOVER_WAIT_MS,
	withdrawSessionLeaseTakeover,
} from "./coding-agent/src/session/session-storage";

/**
 * Ask the writer of the journal at `path` to release it, then wait until its
 * lease is free. Resolves once nobody holds it (the caller opens it next);
 * throws {@link SessionLeaseHeldError} when the holder does not let go in time
 * (an older omp, a hung process). Never takes a lease by force. The holder stops
 * its reply in progress: an Ombra window aborts its run before it closes.
 */
export async function takeSessionLease(path: string, signal: AbortSignal, timeoutMs = SESSION_TAKEOVER_WAIT_MS): Promise<void> {
	const sessionId = await readSessionHeaderId(path);
	if (!sessionId) throw new Error(`Not a session journal: ${path}`);
	if (isSessionLeaseFree(sessionId)) return;
	const request = requestSessionLeaseTakeover(sessionId);
	const deadline = Date.now() + timeoutMs;
	try {
		while (!isSessionLeaseFree(sessionId)) {
			signal.throwIfAborted();
			if (Date.now() >= deadline)
				throw new SessionLeaseHeldError(sessionId, readSessionLeaseHolder(sessionId), "The other writer did not release the session");
			await Bun.sleep(100);
		}
	} finally {
		withdrawSessionLeaseTakeover(sessionId, request);
	}
}
