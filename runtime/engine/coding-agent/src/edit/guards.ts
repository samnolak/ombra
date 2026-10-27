/**
 * Post-edit guards (`edit.guards.*`), all warn-only:
 *
 * - dropped lines: a replaced range lost original lines whose text appears
 *   nowhere in the replacement and that were not deleted explicitly — the
 *   signature of a replace (`PUT N.=N`) used where an insert was meant;
 * - rejection context: a hashline edit rejected for undisplayed or stale
 *   anchors gets the current lines around the range with a fresh tag, recorded
 *   as displayed, so the retry needs no extra read;
 * - syntax check: syntax errors the edit introduced, with their lines
 *   (tree-sitter culprit hunks, plus Python indentation, which tree-sitter's
 *   error recovery accepts).
 */
import * as path from "node:path";
import { blockRangeAt, diffLineRuns, type EditStore } from "@oh-my-pi/pi-natives";
import type { EditMode } from "@oh-my-pi/pi-tui/tools/edit";
import { buildHunks, type EditHunk, locateParseRegression } from "./auto-repair";

/** Dropped lines listed in one warning. */
const MAX_LISTED_DROPPED = 5;
/** Context rows on each side of a rejected range. */
const REJECTION_CONTEXT_LINES = 3;
/** Row budget for one file's rejection context; larger ranges show only their edges. */
const MAX_REJECTION_ROWS = 60;
/** Rows shown around each new syntax error. */
const SYNTAX_CONTEXT_LINES = 1;
/** Row budget for the syntax warning's excerpt. */
const MAX_SYNTAX_ROWS = 24;
/** Tree-sitter culprit search: pairs only for small edits, so the guard stays at O(hunks) parses. */
const SYNTAX_PAIR_SEARCH_HUNKS = 6;

/** 1-based inclusive line span. */
interface LineSpan {
	start: number;
	end: number;
}

/** A pre-edit line the edit removed. */
export interface DroppedLine {
	/** 1-based line number in the pre-edit file. */
	line: number;
	text: string;
}

/** Modes whose replacements can silently drop lines; patch/apply_patch spell every deletion out. */
export function reportsDroppedLines(mode: EditMode): boolean {
	return mode === "hashline" || mode === "replace" || mode === "sloppy";
}

const HASHLINE_HEADER = /^\[([^#\r\n]+)#[0-9A-Fa-f]{4}\]\s*$/;
const HASHLINE_CUT = /^CUT\s+(\d+)(?:\s*\.=\s*(\d+)|\s*(\*))?/;

/** A `CUT` op of a hashline payload, in the section's (pre-edit) line numbers. */
interface HashlineCut {
	/** Section path as authored. */
	path: string;
	start: number;
	/** Inclusive end; undefined for a block cut (`CUT N*`). */
	end?: number;
}

function hashlineCuts(input: string): HashlineCut[] {
	const cuts: HashlineCut[] = [];
	let section: string | undefined;
	for (const raw of input.split("\n")) {
		const line = raw.trimEnd();
		const header = HASHLINE_HEADER.exec(line);
		if (header) {
			section = header[1].trim();
			continue;
		}
		const cut = section === undefined ? null : HASHLINE_CUT.exec(line);
		if (!cut || section === undefined) continue;
		const start = Number(cut[1]);
		cuts.push({ path: section, start, end: cut[3] ? undefined : Number(cut[2] ?? cut[1]) });
	}
	return cuts;
}

/** The file one guard looks at: native outcome fields plus its pre/post text. */
export interface GuardedFile {
	/** Absolute path. */
	path: string;
	displayPath: string;
	prev: string;
	next: string;
}

/**
 * Pre-edit lines removed by replacements whose trimmed text appears nowhere in
 * the edit's added lines. Explicit deletions are excluded: hashline `CUT`
 * ranges, and for replace/sloppy pure-deletion hunks (the model wrote an empty
 * replacement). Blank and punctuation-only lines are ignored.
 */
export function findDroppedLines(mode: EditMode, file: GuardedFile, input: string, cwd: string): DroppedLine[] {
	if (!reportsDroppedLines(mode)) return [];
	const { hunks, a, b } = buildHunks(file.prev, file.next);
	if (hunks.length === 0) return [];

	const explicit: LineSpan[] = [];
	if (mode === "hashline") {
		for (const cut of hashlineCuts(input)) {
			if (cut.path !== file.displayPath && path.resolve(cwd, cut.path) !== file.path) continue;
			explicit.push({ start: cut.start, end: cut.end ?? blockCutEnd(file, cut.start, hunks) });
		}
	}

	const added = new Set<string>();
	for (const h of hunks) for (let i = h.bStart; i < h.bEnd; i++) added.add(b[i].trim());

	const dropped: DroppedLine[] = [];
	for (const h of hunks) {
		if (mode !== "hashline" && h.bEnd === h.bStart) continue;
		for (let i = h.aStart; i < h.aEnd; i++) {
			const text = a[i];
			const trimmed = text.trim();
			if (!/\w/.test(trimmed) || added.has(trimmed)) continue;
			const line = i + 1;
			if (explicit.some(span => line >= span.start && line <= span.end)) continue;
			dropped.push({ line, text: text.replace(/\r$/, "") });
		}
	}
	return dropped;
}

/** Last line of a `CUT N*` block: the tree-sitter block at N, else the removed run starting at N. */
function blockCutEnd(file: GuardedFile, start: number, hunks: readonly EditHunk[]): number {
	const block = blockRangeAt({ code: file.prev, path: file.path, line: start });
	if (block && block.startLine === start) return block.endLine;
	const hunk = hunks.find(h => start - 1 >= h.aStart && start - 1 < h.aEnd);
	return hunk ? hunk.aEnd : start;
}

/** Model-facing warning for {@link findDroppedLines}; undefined when nothing was dropped. */
export function formatDroppedLinesWarning(
	mode: EditMode,
	display: string,
	dropped: readonly DroppedLine[],
): string | undefined {
	if (dropped.length === 0) return undefined;
	const rows = dropped.slice(0, MAX_LISTED_DROPPED).map(d => `  ${d.line}:${d.text}`);
	if (dropped.length > MAX_LISTED_DROPPED) rows.push(`  … ${dropped.length - MAX_LISTED_DROPPED} more`);
	const hint =
		mode === "hashline"
			? "If you meant to insert, use PUT >N / PUT <N; restore lines you meant to keep."
			: mode === "sloppy"
				? "If you meant to insert, use *** Insert Before / *** Insert After; restore lines you meant to keep."
				: "If you meant to insert, keep the anchor text in new_string; restore lines you meant to keep.";
	return [
		`Warning: this edit removed ${dropped.length} line(s) of ${display} that appear nowhere in the replacement (pre-edit line numbers):`,
		...rows,
		hint,
	].join("\n");
}

// ─── Syntax check ───────────────────────────────────────────────────────

/** One syntax error the edit introduced, in post-edit lines. */
export interface SyntaxIssue {
	start: number;
	end: number;
	message: string;
}

const PYTHON_PATH = /\.(?:py|pyi|pyw)$/i;

/**
 * Python indentation errors a tree-sitter parse accepts: unexpected indent,
 * missing block after `:`, and dedents matching no outer level. A linear scan
 * that tracks strings, comments, brackets and backslash continuations, so only
 * logical-line starts are measured. Tabs advance to the next multiple of 8, as
 * in CPython. Lines are 1-based.
 */
export function pythonIndentationIssues(source: string): { line: number; message: string }[] {
	const issues: { line: number; message: string }[] = [];
	const stack = [0];
	let depth = 0;
	let quote: string | undefined;
	let continued = false;
	let opener = false;
	let lastSig = "";
	const lines = source.split("\n");
	for (let i = 0; i < lines.length; i++) {
		const line = lines[i].endsWith("\r") ? lines[i].slice(0, -1) : lines[i];
		let pos = 0;
		if (quote === undefined && depth === 0 && !continued) {
			let col = 0;
			while (pos < line.length && (line[pos] === " " || line[pos] === "\t" || line[pos] === "\f")) {
				col = line[pos] === "\t" ? (Math.floor(col / 8) + 1) * 8 : line[pos] === " " ? col + 1 : 0;
				pos++;
			}
			// Blank and comment-only lines carry no indentation.
			if (pos === line.length || line[pos] === "#") continue;
			const top = stack[stack.length - 1];
			if (opener && col <= top) issues.push({ line: i + 1, message: "expected an indented block" });
			if (col > top) {
				if (!opener) issues.push({ line: i + 1, message: "unexpected indent" });
				stack.push(col);
			} else if (col < top) {
				while (stack.length > 1 && stack[stack.length - 1] > col) stack.pop();
				if (stack[stack.length - 1] !== col) {
					issues.push({ line: i + 1, message: "unindent does not match any outer indentation level" });
					stack.push(col);
				}
			}
			opener = false;
			lastSig = "";
		}
		continued = false;
		while (pos < line.length) {
			const ch = line[pos];
			if (quote !== undefined) {
				if (ch === "\\") {
					if (pos === line.length - 1) continued = true;
					pos += 2;
				} else if (line.startsWith(quote, pos)) {
					pos += quote.length;
					quote = undefined;
					lastSig = ch;
				} else {
					pos++;
				}
				continue;
			}
			if (ch === "#") break;
			if (ch === '"' || ch === "'") {
				quote = line.startsWith(ch.repeat(3), pos) ? ch.repeat(3) : ch;
				pos += quote.length;
				continue;
			}
			if (ch === "\\" && pos === line.length - 1) {
				continued = true;
				break;
			}
			if (ch === "(" || ch === "[" || ch === "{") depth++;
			else if (ch === ")" || ch === "]" || ch === "}") depth = Math.max(0, depth - 1);
			if (ch !== " " && ch !== "\t") lastSig = ch;
			pos++;
		}
		// A single-quoted string ends at the newline unless escaped.
		if (quote !== undefined && quote.length === 1 && !continued) quote = undefined;
		if (quote === undefined && depth === 0 && !continued) opener = lastSig === ":";
	}
	return issues;
}

/** Post-edit line → pre-edit line for unchanged lines (1-based). */
function unchangedLineMap(prev: string, next: string): Map<number, number> {
	const map = new Map<number, number>();
	let ai = 0;
	let bi = 0;
	for (const run of diffLineRuns(prev, next)) {
		if (run.added) bi += run.count;
		else if (run.removed) ai += run.count;
		else {
			for (let k = 0; k < run.count; k++) map.set(bi + k + 1, ai + k + 1);
			ai += run.count;
			bi += run.count;
		}
	}
	return map;
}

/**
 * Syntax errors present after the edit and not before. `parseRegressed` is the
 * native verdict (parsed before, not after); its culprit hunks are located by
 * reverting hunks until the file parses (every changed hunk when that fails).
 * Python files are also checked for indentation errors, compared line by line
 * against the pre-edit file.
 */
export function findNewSyntaxIssues(file: GuardedFile, parseRegressed: boolean): SyntaxIssue[] {
	const issues: SyntaxIssue[] = [];
	if (parseRegressed) {
		const hunks = locateParseRegression(file, SYNTAX_PAIR_SEARCH_HUNKS) ?? buildHunks(file.prev, file.next).hunks;
		const lineCount = Math.max(1, fileLines(file.next).length);
		for (const h of hunks) {
			// A pure deletion has no post-image lines: point at the line after the gap.
			const start = Math.min(h.bStart + 1, lineCount);
			const end = Math.max(start, Math.min(h.bEnd, lineCount));
			issues.push({ start, end, message: "parse error introduced by this change" });
		}
	}
	if (PYTHON_PATH.test(file.path)) {
		const before = new Set(pythonIndentationIssues(file.prev).map(issue => `${issue.line}\0${issue.message}`));
		const map = unchangedLineMap(file.prev, file.next);
		for (const issue of pythonIndentationIssues(file.next)) {
			const old = map.get(issue.line);
			if (old !== undefined && before.has(`${old}\0${issue.message}`)) continue;
			issues.push({ start: issue.line, end: issue.line, message: issue.message });
		}
	}
	return issues.sort((x, y) => x.start - y.start);
}

/** Merge spans that overlap or touch. Input need not be sorted. */
function mergeSpans(spans: LineSpan[]): LineSpan[] {
	const sorted = [...spans].sort((x, y) => x.start - y.start);
	const merged: LineSpan[] = [];
	for (const span of sorted) {
		const last = merged.at(-1);
		if (last && span.start <= last.end + 1) last.end = Math.max(last.end, span.end);
		else merged.push({ ...span });
	}
	return merged;
}

/** Numbered rows for `spans` of `lines`, `…` between gaps, capped at `maxRows`; returns the shown line numbers. */
function renderRows(lines: readonly string[], spans: LineSpan[], maxRows: number): { rows: string[]; shown: number[] } {
	const rows: string[] = [];
	const shown: number[] = [];
	for (const span of mergeSpans(spans)) {
		if (shown.length >= maxRows) break;
		if (rows.length > 0) rows.push("…");
		for (let n = span.start; n <= span.end && shown.length < maxRows; n++) {
			rows.push(`${n}:${lines[n - 1].replace(/\r$/, "")}`);
			shown.push(n);
		}
	}
	return { rows, shown };
}

/** Lines of `text` without the empty element a trailing newline leaves. */
function fileLines(text: string): string[] {
	const lines = text.split("\n");
	if (lines.length > 1 && lines[lines.length - 1] === "") lines.pop();
	return lines;
}

/**
 * Model-facing warning for {@link findNewSyntaxIssues}, with the affected
 * post-edit lines. In hashline mode the excerpt carries the file's current tag
 * and its rows are recorded as displayed, so a fix needs no re-read.
 */
export function formatSyntaxWarning(options: {
	mode: EditMode;
	display: string;
	file: GuardedFile;
	issues: readonly SyntaxIssue[];
	store: EditStore;
}): string | undefined {
	const { mode, display, file, issues, store } = options;
	if (issues.length === 0) return undefined;
	const lines = fileLines(file.next);
	const list = issues.map(
		issue => `  ${issue.start === issue.end ? `line ${issue.start}` : `lines ${issue.start}-${issue.end}`}: ${issue.message}`,
	);
	const spans = issues.map(issue => ({
		start: Math.max(1, issue.start - SYNTAX_CONTEXT_LINES),
		end: Math.min(lines.length, issue.end + SYNTAX_CONTEXT_LINES),
	}));
	const { rows, shown } = renderRows(lines, spans, MAX_SYNTAX_ROWS);
	let header = `[${display}]`;
	if (mode === "hashline") {
		const tag = store.headHash(file.path);
		if (tag && store.byHashText(file.path, tag) === file.next) {
			store.recordSeenLines(file.path, tag, shown);
			header = `[${display}#${tag}]`;
		}
	}
	return [
		`Warning: this edit introduced syntax errors in ${display} (the edit was applied):`,
		...list,
		header,
		...rows,
		"Fix these lines before moving on.",
	].join("\n");
}

// ─── Rejection context ──────────────────────────────────────────────────

const NEVER_DISPLAYED = /This edit anchors to lines ([\d ,-]+?) of (.+?) that \[[^\]\n]+#[0-9A-Fa-f]{4}\] never displayed/g;
const REJECTED_FOR = /^Edit rejected for (.+?): /gm;
const MARKED_ROW = /^\*(\d+):/gm;

/** File + referenced line spans of one anchor rejection. */
interface RejectedReference {
	display: string;
	spans: LineSpan[];
}

function parseLineList(list: string): LineSpan[] {
	const spans: LineSpan[] = [];
	for (const part of list.split(",")) {
		const match = /^\s*(\d+)(?:\s*-\s*(\d+))?\s*$/.exec(part);
		if (!match) continue;
		const start = Number(match[1]);
		spans.push({ start, end: Math.max(start, Number(match[2] ?? match[1])) });
	}
	return spans;
}

/**
 * References in a native hashline rejection: "anchors to lines … never
 * displayed" lists its lines; stale-tag rejections ("Edit rejected for P: …")
 * mark the referenced lines of their excerpt with `*N:`.
 */
function rejectedReferences(text: string): RejectedReference[] {
	const refs: RejectedReference[] = [];
	for (const match of text.matchAll(NEVER_DISPLAYED)) {
		const spans = parseLineList(match[1]);
		if (spans.length > 0) refs.push({ display: match[2], spans });
	}
	const starts = [...text.matchAll(REJECTED_FOR)];
	for (let k = 0; k < starts.length; k++) {
		const from = starts[k].index ?? 0;
		const block = text.slice(from, starts[k + 1]?.index ?? text.length);
		const spans = [...block.matchAll(MARKED_ROW)].map(row => ({ start: Number(row[1]), end: Number(row[1]) }));
		if (spans.length > 0) refs.push({ display: starts[k][1], spans });
	}
	return refs;
}

/**
 * Current lines (±3) around the ranges a rejected hashline edit referenced,
 * under the file's current tag. The rows are recorded as displayed, so a
 * corrected edit with this header passes the seen-line guard without a read.
 * Undefined when the rejection names no anchored lines.
 */
export async function hashlineRejectionContext(text: string, cwd: string, store: EditStore): Promise<string | undefined> {
	const blocks: string[] = [];
	const seen = new Set<string>();
	for (const ref of rejectedReferences(text)) {
		const absolute = path.resolve(cwd, ref.display);
		if (seen.has(absolute)) continue;
		seen.add(absolute);
		let content: string;
		try {
			content = await Bun.file(absolute).text();
		} catch {
			continue;
		}
		const lines = fileLines(content);
		if (lines.length === 0) continue;
		const clamp = (n: number) => Math.min(Math.max(n, 1), lines.length);
		const wide = ref.spans.map(span => ({
			start: clamp(span.start - REJECTION_CONTEXT_LINES),
			end: clamp(span.end + REJECTION_CONTEXT_LINES),
		}));
		const total = mergeSpans(wide).reduce((sum, span) => sum + span.end - span.start + 1, 0);
		// Oversized ranges: show each range's edges, where retries usually go wrong.
		const spans =
			total <= MAX_REJECTION_ROWS
				? wide
				: ref.spans.flatMap(span => [
						{ start: clamp(span.start - REJECTION_CONTEXT_LINES), end: clamp(span.start + REJECTION_CONTEXT_LINES) },
						{ start: clamp(span.end - REJECTION_CONTEXT_LINES), end: clamp(span.end + REJECTION_CONTEXT_LINES) },
					]);
		const { rows, shown } = renderRows(lines, spans, MAX_REJECTION_ROWS);
		const tag = store.recordSnapshot(absolute, content, shown);
		blocks.push([`[${ref.display}#${tag}]`, ...rows].join("\n"));
	}
	if (blocks.length === 0) return undefined;
	return [
		"Current lines around the referenced range (fresh tag; these rows now count as displayed, so re-issue a corrected edit with this header and these line numbers — no re-read needed):",
		...blocks,
	].join("\n");
}
