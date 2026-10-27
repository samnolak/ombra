/**
 * Session-scoped native edit state: full-file snapshots that mint hashline
 * tags (recorded by `read`/`search`/`write`), `CUT`/`PUT` clipboard
 * registers, and the byte-identical no-op loop guard. One store per
 * {@link ToolSession}; every {@link EditSession} the edit tool opens shares it.
 */
import { diffLineRuns, EditStore } from "@oh-my-pi/pi-natives";

/** Owner of the lazily created per-session store. */
export interface EditStoreOwner {
	editStore?: EditStore;
}

/** The session's store, created on first use. */
export function getEditStore(session: EditStoreOwner): EditStore {
	session.editStore ??= new EditStore();
	return session.editStore;
}

const SEEN_ROW_PREFIX = /^[ *]?(\d+)(?:-(\d+))?:/;

/**
 * Seen-line entry that no edit can anchor (lines are 1-based). An empty seen set
 * means unrestricted provenance (a `write` tag), so a display whose every row was
 * column-cut records this instead: provenance present, no source line fully shown.
 */
const NO_FULL_LINE = 0;

/** Record numbered output without treating column-cut rows as complete source lines. */
export function recordFullySeenRows(
	store: EditStore,
	path: string,
	tag: string,
	body: string,
	cutLines?: ReadonlySet<number>,
): void {
	if (!cutLines?.size) {
		store.recordSeenLinesFromBody(path, tag, body);
		return;
	}
	store.recordSeenLines(path, tag, [NO_FULL_LINE]);
	const completeRows = body
		.split("\n")
		.filter(row => {
			const prefix = SEEN_ROW_PREFIX.exec(row);
			return !prefix || (!cutLines.has(Number(prefix[1])) && (!prefix[2] || !cutLines.has(Number(prefix[2]))));
		})
		.join("\n");
	store.recordSeenLinesFromBody(path, tag, completeRows);
}

/**
 * `edit.seenFromEditResults`: count the lines an applied edit wrote (added or
 * replaced, 1-based in `next`) as displayed for the file's current tag. The
 * edit result elides the middle of long inserted runs, but the model authored
 * that text, so anchoring a follow-up edit on it needs no re-read. Skipped when
 * the store's head snapshot is not exactly `next` (e.g. a later rewrite).
 */
export function recordAuthoredLines(store: EditStore, path: string, prev: string, next: string): void {
	const tag = store.headHash(path);
	if (!tag || store.byHashText(path, tag) !== next) return;
	const authored: number[] = [];
	let line = 1;
	for (const run of diffLineRuns(prev, next)) {
		if (run.removed) continue;
		if (run.added) for (let k = 0; k < run.count; k++) authored.push(line + k);
		line += run.count;
	}
	if (authored.length > 0) store.recordSeenLines(path, tag, authored);
}
