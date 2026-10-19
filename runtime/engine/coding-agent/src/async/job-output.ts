/**
 * Append-only output of one background job, addressed by UTF-8 byte offsets
 * from the job's first output byte. Only the newest
 * {@link JOB_OUTPUT_RETAINED_BYTES} stay in memory; older bytes keep their
 * offsets, so a reader's cursor stays valid and learns how much it missed.
 */

/** Newest output bytes a job keeps in memory. */
export const JOB_OUTPUT_RETAINED_BYTES = 1024 * 1024;
/** Output bytes every job's log may hold together; settled jobs give theirs up first. */
export const JOB_OUTPUT_BUDGET_BYTES = 16 * 1024 * 1024;

/** One bounded read of a job's output. */
export interface JobOutputSlice {
	/** Byte offset of `text`'s first byte (a character boundary). */
	fromByte: number;
	/** Offset after `text`; pass it as the next `fromByte`. */
	nextByte: number;
	text: string;
}

function isContinuationByte(byte: number | undefined): boolean {
	return byte !== undefined && (byte & 0xc0) === 0x80;
}

export class JobOutputLog {
	readonly #capacity: number;
	#chunks: Buffer[] = [];
	#retained = 0;
	#total = 0;
	#lastOutputAt: number | undefined;

	constructor(capacity = JOB_OUTPUT_RETAINED_BYTES) {
		this.#capacity = Math.max(4, Math.floor(capacity));
	}

	/** Bytes the job has printed since it started. */
	get totalBytes(): number {
		return this.#total;
	}

	/** Offset of the oldest byte still held; earlier bytes were dropped. */
	get startByte(): number {
		return this.#total - this.#retained;
	}

	/** Bytes held in memory. */
	get retainedBytes(): number {
		return this.#retained;
	}

	/** When the job last printed (epoch ms), if ever. */
	get lastOutputAt(): number | undefined {
		return this.#lastOutputAt;
	}

	append(text: string, now = Date.now()): void {
		if (text.length === 0) return;
		const bytes = Buffer.from(text, "utf8");
		this.#chunks.push(bytes);
		this.#retained += bytes.length;
		this.#total += bytes.length;
		this.#lastOutputAt = now;
		this.release(this.#retained - this.#capacity);
	}

	/** Drops up to `bytes` of the oldest held output; offsets of what stays are unchanged. */
	release(bytes: number): void {
		let drop = Math.min(Math.max(0, bytes), this.#retained);
		while (drop > 0) {
			const first = this.#chunks[0]!;
			const cut = Math.min(first.length, drop);
			if (cut === first.length) this.#chunks.shift();
			else this.#chunks[0] = first.subarray(cut);
			this.#retained -= cut;
			drop -= cut;
		}
	}

	/**
	 * Up to `maxBytes` of output starting at `fromByte`, clamped to what is held
	 * and aligned to whole UTF-8 characters: a character cut by the page end
	 * moves to the next read, except that a page always holds at least one whole
	 * character (so `nextByte` advances whenever there is more to read).
	 */
	read(fromByte: number, maxBytes: number): JobOutputSlice {
		const held = this.#bytes();
		let start = Math.min(Math.max(fromByte, this.startByte), this.#total) - this.startByte;
		while (start < held.length && isContinuationByte(held[start])) start++;
		let end = Math.min(held.length, start + Math.max(0, maxBytes));
		if (end < held.length) {
			while (end > start && isContinuationByte(held[end])) end--;
			if (end === start && start < held.length) {
				end = start + 1;
				while (end < held.length && isContinuationByte(held[end])) end++;
			}
		}
		const offset = this.startByte;
		return { fromByte: offset + start, nextByte: offset + end, text: held.subarray(start, end).toString("utf8") };
	}

	/**
	 * The last `maxBytes` of what is held from a character boundary; when that
	 * holds no whole character, the last character (so never empty while output is held).
	 */
	tail(maxBytes: number): JobOutputSlice {
		const held = this.#bytes();
		let start = Math.max(0, held.length - Math.max(0, maxBytes));
		while (start < held.length && isContinuationByte(held[start])) start++;
		if (start === held.length) {
			while (start > 0 && isContinuationByte(held[start - 1])) start--;
			if (start > 0) start--;
			// The oldest held bytes may be the cut-off end of a character: never start inside it
			while (start < held.length && isContinuationByte(held[start])) start++;
		}
		const offset = this.startByte;
		return { fromByte: offset + start, nextByte: offset + held.length, text: held.subarray(start).toString("utf8") };
	}

	#bytes(): Buffer {
		if (this.#chunks.length > 1) this.#chunks = [Buffer.concat(this.#chunks, this.#retained)];
		return this.#chunks[0] ?? Buffer.alloc(0);
	}
}
