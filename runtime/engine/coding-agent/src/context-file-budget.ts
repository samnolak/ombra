/**
 * Byte budget for workspace context files (AGENTS.md and similar) rendered into
 * the system prompt.
 *
 * The user-global chain (files without a `depth`) and the project chain (files
 * with a `depth`) are budgeted separately, so a huge project AGENTS.md cannot
 * push out the user's own instructions. Within a chain, files keep their input
 * order (broad first, nearest last) and the budget is spent nearest-first: the
 * nearest files stay whole, the first file that no longer fits is truncated to
 * the remaining bytes, and broader files are omitted.
 */

/** A context file entry as loaded for the system prompt. */
export interface BudgetedContextFile {
	path: string;
	content: string;
	depth?: number;
}

/** A file whose content was cut to fit the budget. */
export interface TruncatedContextFile {
	path: string;
	originalBytes: number;
	includedBytes: number;
}

export interface ContextFileBudgetResult<T extends BudgetedContextFile> {
	/** Files to render, in input order; truncated entries carry the cut content. */
	files: T[];
	/** Paths of files left out entirely, in input order. */
	omitted: string[];
	/** Files rendered with only a prefix of their content, in input order. */
	truncated: TruncatedContextFile[];
}

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/**
 * Longest prefix of `content` within `maxBytes` UTF-8 bytes. It ends on a line
 * boundary when that keeps at least half the allowance, otherwise on a code
 * point boundary.
 */
function truncateUtf8(content: string, maxBytes: number): string {
	const bytes = encoder.encode(content);
	if (bytes.length <= maxBytes) return content;
	let end = maxBytes;
	// Step back over UTF-8 continuation bytes (10xxxxxx) so no code point is split.
	while (end > 0 && (bytes[end]! & 0xc0) === 0x80) end--;
	const prefix = decoder.decode(bytes.subarray(0, end));
	const lastNewline = prefix.lastIndexOf("\n");
	return lastNewline > 0 && Buffer.byteLength(prefix.slice(0, lastNewline), "utf8") * 2 >= maxBytes
		? prefix.slice(0, lastNewline)
		: prefix;
}

function budgetChain<T extends BudgetedContextFile>(
	chain: readonly T[],
	maxBytes: number,
	kept: Map<T, T>,
	omitted: Set<T>,
	truncated: Map<T, TruncatedContextFile>,
): void {
	let remaining = maxBytes;
	for (let index = chain.length - 1; index >= 0; index--) {
		const file = chain[index]!;
		const size = Buffer.byteLength(file.content, "utf8");
		if (size <= remaining) {
			kept.set(file, file);
			remaining -= size;
			continue;
		}
		const content = remaining > 0 ? truncateUtf8(file.content, remaining) : "";
		if (content.length === 0) {
			omitted.add(file);
		} else {
			const includedBytes = Buffer.byteLength(content, "utf8");
			kept.set(file, { ...file, content });
			truncated.set(file, { path: file.path, originalBytes: size, includedBytes });
		}
		// Broader files of this chain do not fit once a nearer one was cut.
		for (let broader = index - 1; broader >= 0; broader--) omitted.add(chain[broader]!);
		return;
	}
}

/**
 * Fit context files into `maxBytes` of content per chain (user-global, project).
 * A budget of 0 (or any non-positive or non-finite value) means unlimited and
 * returns the input unchanged.
 */
export function applyContextFileBudget<T extends BudgetedContextFile>(
	files: readonly T[],
	maxBytes: number,
): ContextFileBudgetResult<T> {
	if (!(maxBytes > 0) || !Number.isFinite(maxBytes)) {
		return { files: [...files], omitted: [], truncated: [] };
	}
	const kept = new Map<T, T>();
	const omitted = new Set<T>();
	const truncated = new Map<T, TruncatedContextFile>();
	budgetChain(
		files.filter(file => file.depth === undefined),
		maxBytes,
		kept,
		omitted,
		truncated,
	);
	budgetChain(
		files.filter(file => file.depth !== undefined),
		maxBytes,
		kept,
		omitted,
		truncated,
	);
	return {
		files: files.flatMap(file => {
			const entry = kept.get(file);
			return entry === undefined ? [] : [entry];
		}),
		omitted: files.filter(file => omitted.has(file)).map(file => file.path),
		truncated: files.flatMap(file => {
			const entry = truncated.get(file);
			return entry === undefined ? [] : [entry];
		}),
	};
}

/**
 * One-line, model-facing notice naming the omitted and truncated files, or
 * undefined when the budget left every file whole. The text depends only on the
 * budget and the files, so it stays byte-stable across turns.
 */
export function formatContextFileBudgetNotice(
	maxBytes: number,
	result: Pick<ContextFileBudgetResult<BudgetedContextFile>, "omitted" | "truncated">,
): string | undefined {
	if (result.omitted.length === 0 && result.truncated.length === 0) return undefined;
	const parts: string[] = [];
	if (result.truncated.length > 0) {
		parts.push(
			`truncated ${result.truncated.map(file => `${file.path} (${file.includedBytes} of ${file.originalBytes} bytes)`).join(", ")}`,
		);
	}
	if (result.omitted.length > 0) parts.push(`omitted ${result.omitted.join(", ")}`);
	return `Workspace instructions exceeded the ${maxBytes}-byte budget: ${parts.join("; ")}. Read these files in full before work they may govern.`;
}
