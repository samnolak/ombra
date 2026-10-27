/**
 * Ombra's LLM compaction summary contract: the checkpoint prompt sent in place
 * of the upstream summary template, and the guard that rejects a summary which
 * does not shrink the history it replaces.
 */
import type { Tokenizer } from "@oh-my-pi/pi-agent-core";
import type { CompactionPreparation } from "@oh-my-pi/pi-agent-core/compaction";
import { prompt } from "@oh-my-pi/pi-utils";
import * as snapcompact from "@oh-my-pi/snapcompact";
import checkpointSummaryTemplate from "../prompts/system/compaction-checkpoint-summary.md" with { type: "text" };

/**
 * Summary prompt for LLM compaction. Used for first and iterative summaries
 * alike: it carries its own `<previous-summary>` merge rules, because a
 * prompt override also covers the folded windows of an oversized span.
 */
export const CHECKPOINT_SUMMARY_PROMPT = prompt.render(checkpointSummaryTemplate);

/** A generated summary that would not lower the next request's token cost. */
export class CompactionSummaryNotSmallerError extends Error {
	readonly summaryTokens: number;
	readonly sourceTokens: number;

	constructor(summaryTokens: number, sourceTokens: number) {
		super(
			`Compaction summary is not smaller than the history it replaces (${summaryTokens} >= ${sourceTokens} estimated tokens)`,
		);
		this.name = "CompactionSummaryNotSmallerError";
		this.summaryTokens = summaryTokens;
		this.sourceTokens = sourceTokens;
	}
}

/**
 * Estimated tokens of what a summary replaces: the previous summary (and the
 * text of a previous snapcompact archive it migrates), the summarized
 * messages and a split turn's prefix.
 */
export function compactionSourceTokens(preparation: CompactionPreparation, tokenizer: Tokenizer): number {
	const opts = { excludeEncryptedReasoning: true } as const;
	let tokens = tokenizer.countMessages(preparation.messagesToSummarize, opts);
	tokens += tokenizer.countMessages(preparation.turnPrefixMessages, opts);
	if (preparation.previousSummary) tokens += tokenizer.countTokens(preparation.previousSummary);
	const archive = snapcompact.getPreservedArchive(preparation.previousPreserveData);
	const archiveText = archive ? snapcompact.archiveSourceText(archive) : undefined;
	if (archiveText) tokens += tokenizer.countTokens(archiveText);
	return tokens;
}

/**
 * Summaries at or below this size are never rejected. They cannot meaningfully
 * grow the context, and a tiny span (a split turn's short prefix, a cut that
 * only moves past one small turn) legitimately yields a summary larger than
 * its source: that commit is what lets the post-commit progress guard rescue an
 * oversized kept tail.
 */
export const UNJUDGED_SUMMARY_TOKENS = 512;

/**
 * Throw {@link CompactionSummaryNotSmallerError} when `summary` is not smaller
 * than the history it replaces, unless it is within
 * {@link UNJUDGED_SUMMARY_TOKENS}.
 */
export function assertCompactionSummaryShrinks(
	preparation: CompactionPreparation,
	summary: string,
	tokenizer: Tokenizer,
): void {
	const summaryTokens = tokenizer.countTokens(summary);
	if (summaryTokens <= UNJUDGED_SUMMARY_TOKENS) return;
	const sourceTokens = compactionSourceTokens(preparation, tokenizer);
	if (summaryTokens >= sourceTokens) throw new CompactionSummaryNotSmallerError(summaryTokens, sourceTokens);
}
