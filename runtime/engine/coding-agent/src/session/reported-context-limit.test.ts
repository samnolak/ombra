import { describe, expect, test } from "bun:test";
import { reportedContextLimit } from "./reported-context-limit";

describe("reportedContextLimit", () => {
	test("reads the window OpenAI-compatible servers state", () => {
		expect(
			reportedContextLimit(
				"400 This model's maximum context length is 12000 tokens. However, your messages resulted in 17520 tokens.",
			),
		).toBe(12000);
		expect(reportedContextLimit("maximum context length is 32,768 tokens")).toBe(32768);
	});

	test("reads LM Studio and llama.cpp phrasings", () => {
		expect(
			reportedContextLimit("Trying to keep the first 9000 tokens when context the overflows. However, the model is loaded with context length of only 4096 tokens"),
		).toBe(4096);
		expect(
			reportedContextLimit('{"error":{"code":400,"message":"the request exceeds the available context size, try increasing it","type":"exceed_context_size_error","n_prompt_tokens":9000,"n_ctx":8192}}'),
		).toBe(8192);
	});

	test("ignores messages without a stated window and implausible numbers", () => {
		expect(reportedContextLimit(undefined)).toBeUndefined();
		expect(reportedContextLimit("context_length_exceeded")).toBeUndefined();
		expect(reportedContextLimit("prompt is too long: 210000 tokens > 200000 maximum")).toBeUndefined();
		expect(reportedContextLimit("maximum context length is 12 tokens")).toBeUndefined();
	});
});
