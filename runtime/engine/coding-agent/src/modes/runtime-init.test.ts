import { describe, expect, test } from "bun:test";
import type { ExtensionActions } from "../extensibility/extensions/types";
import type { AgentSession } from "../session/agent-session";
import { RpcExtensionUserMessageTracker, watchRpcInputAdmission } from "./rpc/rpc-prompt-results";
import { initializeExtensions } from "./runtime-init";

async function initializeSendHarness(sends: Pick<AgentSession, "sendUserMessage" | "sendCustomMessage">) {
	let actions: ExtensionActions | undefined;
	const session = {
		...sends,
		extensionRunner: {
			initialize(next: ExtensionActions) { actions = next; },
			onError() {},
			async emit() {},
		},
	} as unknown as AgentSession;
	const tracker = new RpcExtensionUserMessageTracker();
	const errors: Error[] = [];
	await initializeExtensions(session, {
		reportSendError: (_action, error) => errors.push(error),
		reportRuntimeError: error => { throw new Error(error.error); },
		trackAgentInvokingMessage: (task, admission) => tracker.trackAgentMessageTask(task, admission),
	});
	if (!actions) throw new Error("Extension actions were not initialized");
	return { actions, tracker, errors };
}

describe("extension runtime admission wiring", () => {
	for (const deliverAs of [undefined, "steer", "followUp", "aside"] as const) {
		test(`sendUserMessage ${deliverAs ?? "default"} acknowledges prepared delivery before turn completion`, async () => {
			const prepared = Promise.withResolvers<void>();
			const fullTurn = Promise.withResolvers<void>();
			const queued: string[] = [];
			const harness = await initializeSendHarness({
				sendUserMessage: async (_content, options) => {
					expect(options?.deliverAs).toBe(deliverAs);
					await prepared.promise;
					queued.push("message");
					options?.onPromptAdmitted?.();
					await fullTurn.promise;
				},
				sendCustomMessage: async () => false,
			});
			const ack = watchRpcInputAdmission({
				extensionUserMessageTracker: harness.tracker,
				startInput: async () => {
					harness.actions.sendUserMessage("message", { deliverAs });
					return "local";
				},
			}).then(result => {
				expect(queued).toEqual(["message"]);
				return result;
			});
			prepared.resolve();
			expect(await ack).toEqual({ inputDisposition: "local", agentInvoked: true });
			expect(harness.errors).toEqual([]);
			fullTurn.resolve();
		});
	}

	for (const sendOptions of [{ triggerTurn: true }, { deliverAs: "aside" as const }]) {
		test(`sendMessage ${sendOptions.triggerTurn ? "triggerTurn" : "aside"} counts streaming queue insertion even when no fresh turn starts`, async () => {
			const prepared = Promise.withResolvers<void>();
			let queued = false;
			const harness = await initializeSendHarness({
				sendUserMessage: async () => {},
				sendCustomMessage: async (_message, options) => {
					await prepared.promise;
					queued = true;
					options?.onPromptAdmitted?.();
					return false;
				},
			});
			const ack = watchRpcInputAdmission({
				extensionUserMessageTracker: harness.tracker,
				startInput: async () => {
					harness.actions.sendMessage({ customType: "test", content: "queued", display: false }, sendOptions);
					return "local";
				},
			}).then(result => {
				expect(queued).toBe(true);
				return result;
			});
			prepared.resolve();
			expect(await ack).toEqual({ inputDisposition: "local", agentInvoked: true });
			expect(harness.errors).toEqual([]);
		});
	}

	test("triggerTurn custom send does not wait for its full turn to acknowledge", async () => {
		const fullTurn = Promise.withResolvers<void>();
		const harness = await initializeSendHarness({
			sendUserMessage: async () => {},
			sendCustomMessage: async (_message, options) => {
				options?.onPromptAdmitted?.();
				await fullTurn.promise;
				return true;
			},
		});
		const ack = await watchRpcInputAdmission({
			extensionUserMessageTracker: harness.tracker,
			startInput: async () => {
				harness.actions.sendMessage({ customType: "test", content: "turn", display: false }, { triggerTurn: true });
				return "local";
			},
		});
		expect(ack).toEqual({ inputDisposition: "local", agentInvoked: true });
		fullTurn.resolve();
	});

	test("folded aside and deferred triggerTurn sends remain local without synthetic failures", async () => {
		const harness = await initializeSendHarness({
			sendUserMessage: async () => {},
			sendCustomMessage: async () => false,
		});
		const ack = await watchRpcInputAdmission({
			extensionUserMessageTracker: harness.tracker,
			startInput: async () => {
				harness.actions.sendMessage({ customType: "test", content: "fold", display: false }, { deliverAs: "aside" });
				harness.actions.sendMessage({ customType: "test", content: "defer", display: false }, { triggerTurn: true });
				return "local";
			},
		});
		expect(ack).toEqual({ inputDisposition: "local", agentInvoked: false });
		expect(harness.errors).toEqual([]);
	});

	test("cancelled user and custom sends preserve the originating input", async () => {
		for (const sendKind of ["user", "custom"] as const) {
			const harness = await initializeSendHarness({
				sendUserMessage: async () => {},
				sendCustomMessage: async (_message, options) => {
					options?.onPromptCancelled?.();
					return false;
				},
			});
			const ack = await watchRpcInputAdmission({
				extensionUserMessageTracker: harness.tracker,
				startInput: async () => {
					if (sendKind === "user") harness.actions.sendUserMessage("cancelled");
					else harness.actions.sendMessage(
						{ customType: "test", content: "cancelled", display: false },
						{ triggerTurn: true },
					);
					return "local";
				},
			});
			expect(ack).toEqual({ inputDisposition: "cancelled", agentInvoked: false });
		}
	});

	test("an admitted sibling wins over a cancelled nested send", async () => {
		const harness = await initializeSendHarness({
			sendUserMessage: async () => {},
			sendCustomMessage: async (_message, options) => {
				options?.onPromptAdmitted?.();
				return false;
			},
		});
		const ack = await watchRpcInputAdmission({
			extensionUserMessageTracker: harness.tracker,
			startInput: async () => {
				harness.actions.sendUserMessage("cancelled");
				harness.actions.sendMessage({ customType: "test", content: "queued", display: false }, { triggerTurn: true });
				return "local";
			},
		});
		expect(ack).toEqual({ inputDisposition: "local", agentInvoked: true });
	});

	test("nested sendMessage during sendUserMessage preparation delays the original ACK", async () => {
		const parentPrepared = Promise.withResolvers<void>();
		const childPrepared = Promise.withResolvers<void>();
		const childStarted = Promise.withResolvers<void>();
		const fullTurn = Promise.withResolvers<void>();
		const queue: string[] = [];
		const harness = await initializeSendHarness({
			sendUserMessage: async (_content, options) => {
				await parentPrepared.promise;
				harness.actions.sendMessage({ customType: "test", content: "child", display: false }, { triggerTurn: true });
				queue.push("parent");
				options?.onPromptAdmitted?.();
				await fullTurn.promise;
			},
			sendCustomMessage: async (_message, options) => {
				childStarted.resolve();
				await childPrepared.promise;
				queue.push("child");
				options?.onPromptAdmitted?.();
				return false;
			},
		});
		const ack = watchRpcInputAdmission({
			extensionUserMessageTracker: harness.tracker,
			startInput: async () => {
				harness.actions.sendUserMessage("parent");
				return "local";
			},
		}).then(result => {
			expect(queue).toEqual(["parent", "child"]);
			return result;
		});
		parentPrepared.resolve();
		await childStarted.promise;
		childPrepared.resolve();
		expect(await ack).toEqual({ inputDisposition: "local", agentInvoked: true });
		fullTurn.resolve();
	});
});
