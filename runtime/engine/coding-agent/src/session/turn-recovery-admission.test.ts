import { describe, expect, test } from "bun:test";
import { AgentBusyError } from "@oh-my-pi/pi-agent-core";
import { TurnRecovery, type TurnRecoveryHost } from "./turn-recovery";

describe("prompt admission through idle retry", () => {
	test("a busy attempt waits for actual dispatch without waiting for the new full turn", async () => {
		const previousTurn = Promise.withResolvers<void>();
		const waitingForIdle = Promise.withResolvers<void>();
		const nextTurn = Promise.withResolvers<void>();
		const admitted = Promise.withResolvers<void>();
		let attempts = 0;
		let admissions = 0;
		const state = { isStreaming: true };
		const host = {
			agent: {
				state,
				async prompt() {
					attempts++;
					if (state.isStreaming) throw new AgentBusyError();
					state.isStreaming = true;
					await nextTurn.promise;
					state.isStreaming = false;
				},
				async waitForIdle() {
					waitingForIdle.resolve();
					await previousTurn.promise;
				},
			},
		} as unknown as TurnRecoveryHost;
		const recovery = new TurnRecovery(host, { deferFallbackChainValidation: true });
		const turn = recovery.promptAgentWithIdleRetry([], undefined, () => {
			admissions++;
			admitted.resolve();
		});
		await waitingForIdle.promise;
		expect(attempts).toBe(1);
		expect(admissions).toBe(0);
		state.isStreaming = false;
		previousTurn.resolve();
		await admitted.promise;
		expect(attempts).toBe(2);
		expect(admissions).toBe(1);
		expect(state.isStreaming).toBe(true);
		nextTurn.resolve();
		await turn;
		expect(admissions).toBe(1);
	});

	test("an immediate rejected prompt does not report admission", async () => {
		let admissions = 0;
		const host = {
			agent: {
				state: { isStreaming: false },
				async prompt() { throw new Error("model unavailable"); },
			},
		} as unknown as TurnRecoveryHost;
		const recovery = new TurnRecovery(host, { deferFallbackChainValidation: true });
		await expect(recovery.promptAgentWithIdleRetry([], undefined, () => { admissions++; }))
			.rejects.toThrow("model unavailable");
		expect(admissions).toBe(0);
	});
});
