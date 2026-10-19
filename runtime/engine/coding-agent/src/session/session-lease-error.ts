import type { SessionLeaseHolder } from "./session-storage";

/** Another writer holds the session's lease: open it read-only, or ask that writer to let go. */
export class SessionLeaseHeldError extends Error {
	readonly code = "session_lease_held";
	readonly data: { sessionId: string; holder: SessionLeaseHolder | null };

	constructor(sessionId: string, holder: SessionLeaseHolder | undefined, message = "The session is open in another window") {
		super(message);
		this.name = "SessionLeaseHeldError";
		this.data = { sessionId, holder: holder ?? null };
	}
}
