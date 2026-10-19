import type { AstReplaceChange } from "@oh-my-pi/pi-natives";

/** Native AST ranges are UTF-8 byte offsets, not LSP UTF-16 columns. */
export function applyAstPreview(bytes: Uint8Array, changes: readonly AstReplaceChange[]): Buffer {
	const original = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	const spans = changes.map(change => {
		if (!Number.isSafeInteger(change.byteStart) || !Number.isSafeInteger(change.byteEnd) || !Number.isSafeInteger(change.deletedLength) || change.byteStart < 0 || change.byteEnd < change.byteStart || change.deletedLength < 0) {
			throw new Error("Invalid native AST replacement range");
		}
		return { change, before: Buffer.from(change.before), after: Buffer.from(change.after), end: change.byteStart + change.deletedLength };
	});
	const sorted = [...spans].sort((a, b) => a.change.byteStart - b.change.byteStart);
	let previousEnd = 0;
	const originalCoordinates = sorted.every(span => {
		const valid = span.change.byteStart >= previousEnd && span.end <= original.length && span.change.byteEnd <= original.length && original.subarray(span.change.byteStart, span.change.byteEnd).equals(span.before);
		previousEnd = span.end;
		return valid;
	});
	if (originalCoordinates) {
		const chunks: Uint8Array[] = [];
		let offset = 0;
		for (const span of sorted) {
			chunks.push(original.subarray(offset, span.change.byteStart), span.after);
			offset = span.end;
		}
		chunks.push(original.subarray(offset));
		return Buffer.concat(chunks);
	}
	// Multiple rewrite rules can report later ranges in the already-rewritten text.
	// Replay those in their declared order, verifying every exact preimage.
	let result = original;
	for (const span of spans) {
		if (span.end > result.length || span.change.byteEnd > result.length || !result.subarray(span.change.byteStart, span.change.byteEnd).equals(span.before)) {
			throw new Error("Stale or incomplete native AST preview; no files were changed");
		}
		result = Buffer.concat([result.subarray(0, span.change.byteStart), span.after, result.subarray(span.end)]);
	}
	return result;
}
