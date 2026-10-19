import { describe, expect, test } from "bun:test";
import type { AssistantMessage } from "@oh-my-pi/pi-ai";
import { RpcSessionEventForwarder } from "./rpc-session-events";
import type { RpcProjectedSessionEventFrame } from "./rpc-types";

function message(narration: boolean): AssistantMessage {
	return {
		role: "assistant", content: [{ type: "thinking", thinking: "Checking now.", narration }],
		api: "anthropic-messages", provider: "anthropic", model: "test",
		usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
		stopReason: "stop", timestamp: 1,
	};
}

describe("RPC narration projection", () => {
	for (const narration of [false, true]) {
		test(`delta thinking_end preserves narration=${narration} without cumulative snapshots`, () => {
			const frames: RpcProjectedSessionEventFrame[] = [];
			const forwarder = new RpcSessionEventForwarder(frame => frames.push(frame));
			forwarder.setFilter(null, "delta");
			const partial = message(narration);
			forwarder.forward({ type: "message_update", message: partial,
				assistantMessageEvent: { type: "thinking_end", contentIndex: 0, content: "Checking now.", partial } });
			expect(frames).toHaveLength(1);
			expect(frames[0]).toEqual({ type: "message_update", messageId: "msg-1", message: { role: "assistant" },
				assistantMessageEvent: { type: "thinking_end", contentIndex: 0, content: "Checking now.",
					...(narration ? { narration: true } : {}) } });
		});
	}

	test("full updates retain the original provider content", () => {
		const frames: RpcProjectedSessionEventFrame[] = [];
		const forwarder = new RpcSessionEventForwarder(frame => frames.push(frame));
		const partial = message(true);
		forwarder.forward({ type: "message_update", message: partial,
			assistantMessageEvent: { type: "thinking_end", contentIndex: 0, content: "Checking now.", partial } });
		expect(frames[0]).toMatchObject({ message: partial, assistantMessageEvent: { partial } });
	});
});
