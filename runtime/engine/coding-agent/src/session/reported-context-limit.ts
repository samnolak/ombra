/**
 * The context window a provider states in its context-overflow error, when it names one.
 *
 * Local servers (vLLM, llama.cpp, LM Studio) are often started with a shorter context than the model's
 * configured `contextWindow`. Their overflow error then names the real limit, while every maintenance
 * threshold still measures against the configured window, so compaction finds "nothing to free" and the
 * run dead-ends. Recognized phrasings:
 * - OpenAI / vLLM: "This model's maximum context length is 8192 tokens."
 * - LM Studio: "the model is loaded with context length of only 4096 tokens"
 * - llama.cpp: `"n_ctx": 4096` (or `n_ctx: 4096`) next to "exceeds the available context size"
 */
const REPORTED_LIMIT_PATTERNS: readonly RegExp[] = [
	/\bmaximum context length is (\d[\d,]*) tokens\b/i,
	/\bcontext length of only (\d[\d,]*) tokens\b/i,
	/\bn_ctx\\?"?\s*[:=]\s*(\d+)\b/i,
];

/** Smallest window worth adopting: anything below is a parse accident, not a real server limit. */
const MIN_REPORTED_CONTEXT_WINDOW = 1024;

export function reportedContextLimit(errorMessage: string | undefined): number | undefined {
	if (!errorMessage) return undefined;
	for (const pattern of REPORTED_LIMIT_PATTERNS) {
		const match = pattern.exec(errorMessage);
		if (!match) continue;
		const tokens = Number(match[1].replaceAll(",", ""));
		if (Number.isSafeInteger(tokens) && tokens >= MIN_REPORTED_CONTEXT_WINDOW) return tokens;
	}
	return undefined;
}
