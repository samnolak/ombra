import { describe, expect, test } from "bun:test";
import {
	RpcExtensionUserMessageTracker,
	RpcPromptResults,
	watchAndReportPromptResult,
	watchRpcInputAdmission,
} from "./rpc-prompt-results";
import type { RpcInputDisposition, RpcPromptResultFrame } from "./rpc-types";

const idleSession = {
	isStreaming: false,
	hasAdmittedSubmission: false,
	queuedMessageCount: 0,
	hasPendingAsyncWork: () => false,
};

describe("causal RPC input admission", () => {
	for (const inputKind of ["prompt", "steer", "follow_up"] as const) {
		test(`${inputKind} acknowledges after queue insertion, not after the turn`, async () => {
			const preparation = Promise.withResolvers<void>();
			const fullTurn = Promise.withResolvers<void>();
			const queue: string[] = [];
			const ack = watchRpcInputAdmission({
				extensionUserMessageTracker: new RpcExtensionUserMessageTracker(),
				startInput: async onPromptAdmitted => {
					await preparation.promise;
					queue.push(inputKind);
					onPromptAdmitted();
					await fullTurn.promise;
					return "admitted";
				},
			}).then(result => {
				expect(queue).toEqual([inputKind]);
				return result;
			});
			preparation.resolve();
			expect(await ack).toEqual({ inputDisposition: "admitted", agentInvoked: true });
			fullTurn.resolve();
		});
	}

	for (const disposition of ["local", "cancelled"] as const) {
		test(`${disposition} input never invents agent invocation`, async () => {
			const ack = await watchRpcInputAdmission({
				extensionUserMessageTracker: new RpcExtensionUserMessageTracker(),
				startInput: async () => disposition,
			});
			expect(ack).toEqual({ inputDisposition: disposition, agentInvoked: false });
		});
	}

	test("a completed SDK call without admission is cancelled", async () => {
		expect(await watchRpcInputAdmission({
			extensionUserMessageTracker: new RpcExtensionUserMessageTracker(),
			startInput: async () => "admitted",
		})).toEqual({ inputDisposition: "cancelled", agentInvoked: false });
	});

	for (const hookOutcome of ["handled", "transformed-empty"] as const) {
		test(`${hookOutcome} input waits for its extension send's admission, not its turn`, async () => {
			const tracker = new RpcExtensionUserMessageTracker();
			const admission = Promise.withResolvers<RpcInputDisposition>();
			const fullTurn = Promise.withResolvers<void>();
			let queued = false;
			const ack = watchRpcInputAdmission({
				extensionUserMessageTracker: tracker,
				startInput: async () => {
					tracker.trackAgentMessageTask(fullTurn.promise, admission.promise);
					return "local";
				},
			}).then(result => {
				expect(queued).toBe(true);
				return result;
			});
			queued = true;
			admission.resolve("admitted");
			expect(await ack).toEqual({ inputDisposition: "local", agentInvoked: true });
			fullTurn.resolve();
		});
	}

	test("nested asynchronous extension sends remain attached after the handler returns", async () => {
		const tracker = new RpcExtensionUserMessageTracker();
		const parentPreparation = Promise.withResolvers<void>();
		const parentAdmission = Promise.withResolvers<RpcInputDisposition>();
		const nestedAdmission = Promise.withResolvers<RpcInputDisposition>();
		const nestedRegistered = Promise.withResolvers<void>();
		const fullTurn = Promise.withResolvers<void>();
		const queue: string[] = [];
		const ack = watchRpcInputAdmission({
			extensionUserMessageTracker: tracker,
			startInput: async () => {
				const parentSend = (async () => {
					await parentPreparation.promise;
					tracker.trackAgentMessageTask(fullTurn.promise, nestedAdmission.promise);
					queue.push("parent");
					parentAdmission.resolve("admitted");
					nestedRegistered.resolve();
					await fullTurn.promise;
				})();
				tracker.trackAgentMessageTask(parentSend, parentAdmission.promise);
				return "local";
			},
		}).then(result => {
			expect(queue).toEqual(["parent", "nested"]);
			return result;
		});
		parentPreparation.resolve();
		await nestedRegistered.promise;
		queue.push("nested");
		nestedAdmission.resolve("admitted");
		expect(await ack).toEqual({ inputDisposition: "local", agentInvoked: true });
		fullTurn.resolve();
	});

	test("concurrent input scopes do not claim each other's extension sends", async () => {
		const tracker = new RpcExtensionUserMessageTracker();
		const admission = Promise.withResolvers<RpcInputDisposition>();
		const fullTurn = Promise.withResolvers<void>();
		const first = watchRpcInputAdmission({
			extensionUserMessageTracker: tracker,
			startInput: async () => {
				tracker.trackAgentMessageTask(fullTurn.promise, admission.promise);
				return "local";
			},
		});
		const unrelated = await watchRpcInputAdmission({
			extensionUserMessageTracker: tracker,
			startInput: async () => "local",
		});
		expect(unrelated).toEqual({ inputDisposition: "local", agentInvoked: false });
		admission.resolve("admitted");
		expect(await first).toEqual({ inputDisposition: "local", agentInvoked: true });
		fullTurn.resolve();
	});

	test("nested tracking scopes propagate only to their causal ancestors", async () => {
		const tracker = new RpcExtensionUserMessageTracker();
		const outer = tracker.watchPrompt(async () => {
			const inner = tracker.watchPrompt(async () => {
				tracker.trackAgentMessageTask(Promise.resolve(), Promise.resolve("admitted"));
			});
			await inner.prompt;
			await inner.waitForAgentMessageAdmissions();
			expect(inner.hasAgentMessageTask()).toBe(true);
		});
		await outer.prompt;
		await outer.waitForAgentMessageAdmissions();
		expect(outer.hasAgentMessageTask()).toBe(true);
	});

	test("folded or deferred custom messages do not claim an agent turn", async () => {
		const tracker = new RpcExtensionUserMessageTracker();
		expect(await watchRpcInputAdmission({
			extensionUserMessageTracker: tracker,
			startInput: async () => {
				tracker.trackAgentMessageTask(Promise.resolve(false), Promise.resolve("local"));
				return "local";
			},
		})).toEqual({ inputDisposition: "local", agentInvoked: false });
	});

	test("failed extension admission rejects instead of acknowledging nonexistent queued input", async () => {
		const tracker = new RpcExtensionUserMessageTracker();
		const failure = new Error("image preparation failed");
		const ack = watchRpcInputAdmission({
			extensionUserMessageTracker: tracker,
			startInput: async () => {
				tracker.trackAgentMessageTask(Promise.reject(failure), Promise.reject(failure));
				return "local";
			},
		});
		await expect(ack).rejects.toThrow("image preparation failed");
	});

	test("a failed nested send does not withdraw an already admitted direct input", async () => {
		const tracker = new RpcExtensionUserMessageTracker();
		const fullTurn = Promise.withResolvers<void>();
		const failure = new Error("nested preparation failed");
		const ack = await watchRpcInputAdmission({
			extensionUserMessageTracker: tracker,
			startInput: async onPromptAdmitted => {
				tracker.trackAgentMessageTask(Promise.reject(failure), Promise.reject(failure));
				onPromptAdmitted();
				await fullTurn.promise;
				return "admitted";
			},
		});
		expect(ack).toEqual({ inputDisposition: "admitted", agentInvoked: true });
		fullTurn.resolve();
	});

	test("cancelled original input remains recoverable while a nested turn is live", async () => {
		const tracker = new RpcExtensionUserMessageTracker();
		const fullTurn = Promise.withResolvers<void>();
		const ack = await watchRpcInputAdmission({
			extensionUserMessageTracker: tracker,
			startInput: async () => {
				tracker.trackAgentMessageTask(fullTurn.promise, Promise.resolve("admitted"));
				return "cancelled";
			},
		});
		expect(ack).toEqual({ inputDisposition: "cancelled", agentInvoked: true });
		fullTurn.resolve();
	});

	test("a local builtin can start a turn without queueing the original command text", async () => {
		const fullTurn = Promise.withResolvers<void>();
		const ack = await watchRpcInputAdmission({
			extensionUserMessageTracker: new RpcExtensionUserMessageTracker(),
			startInput: async onPromptAdmitted => {
				onPromptAdmitted("local");
				await fullTurn.promise;
				return "local";
			},
		});
		expect(ack).toEqual({ inputDisposition: "local", agentInvoked: true });
		fullTurn.resolve();
	});
});

describe("causal prompt result ordering", () => {
	test("a result is buffered until the transport writes the matching acknowledgement", () => {
		const frames: RpcPromptResultFrame[] = [];
		const results = new RpcPromptResults(idleSession, frame => frames.push(frame));
		const ticket = results.begin("prompt-1");
		results.fail(ticket, "failed after admission");
		expect(frames).toEqual([]);
		results.acknowledge("another-prompt");
		expect(frames).toEqual([]);
		results.acknowledge("prompt-1");
		expect(frames).toHaveLength(1);
		expect(frames[0]).toMatchObject({ id: "prompt-1", status: "error" });
		results.acknowledge("prompt-1");
		results.fail(ticket, "duplicate");
		expect(frames).toHaveLength(1);
	});

	test("discarded id-less input does not release the next input's result", () => {
		const frames: RpcPromptResultFrame[] = [];
		const results = new RpcPromptResults(idleSession, frame => frames.push(frame));
		const local = results.begin(undefined);
		const next = results.begin(undefined);
		results.discard(local);
		results.fail(next, "second input");
		results.acknowledge(undefined);
		expect(frames).toEqual([]);
		results.acknowledge(undefined);
		expect(frames).toHaveLength(1);
	});

	test("late failure notifications stay behind the actual input acknowledgement", () => {
		const order: string[] = [];
		const results = new RpcPromptResults(idleSession, () => order.push("result"));
		const ticket = results.begin("failure");
		results.fail(ticket, "late error", () => order.push("error"));
		expect(order).toEqual([]);
		order.push("ack");
		results.acknowledge("failure");
		expect(order).toEqual(["ack", "error", "result"]);
	});

	test("local commands and cancelled preparation owe no prompt_result", async () => {
		for (const completed of [false, true]) {
			const frames: RpcPromptResultFrame[] = [];
			const errors: Error[] = [];
			const results = new RpcPromptResults(idleSession, frame => frames.push(frame));
			const ack = await watchAndReportPromptResult({
				ticket: results.begin("local"),
				startPrompt: async () => completed,
				results,
				onError: error => errors.push(error),
				extensionUserMessageTracker: new RpcExtensionUserMessageTracker(),
			});
			results.acknowledge("local");
			expect(ack).toEqual({ inputDisposition: completed ? "cancelled" : "local", agentInvoked: false });
			expect(frames).toEqual([]);
			expect(errors).toEqual([]);
		}
	});

	test("local input with an extension-created turn still receives its correlated prompt_result", async () => {
		const session = { ...idleSession, isStreaming: true };
		const frames: RpcPromptResultFrame[] = [];
		const reported = Promise.withResolvers<RpcPromptResultFrame>();
		const results = new RpcPromptResults(session, frame => {
			frames.push(frame);
			reported.resolve(frame);
		});
		const tracker = new RpcExtensionUserMessageTracker();
		const fullTurn = Promise.withResolvers<void>();
		const ack = await watchRpcInputAdmission({
			extensionUserMessageTracker: tracker,
			report: {
				ticket: results.begin("handled-hook"),
				results,
				onError: error => { throw error; },
			},
			startInput: async () => {
				tracker.trackAgentMessageTask(fullTurn.promise, Promise.resolve("admitted"));
				results.observe({ type: "agent_start" });
				return "local";
			},
		});
		expect(ack).toEqual({ inputDisposition: "local", agentInvoked: true });
		results.acknowledge("handled-hook");
		expect(frames).toEqual([]);
		session.isStreaming = false;
		results.observe({ type: "agent_end", messages: [], yielded: true, isTerminal: true });
		fullTurn.resolve();
		expect(await reported.promise).toMatchObject({
			id: "handled-hook",
			agentInvoked: true,
			status: "completed",
		});
		expect(frames).toHaveLength(1);
	});

	test("pre-admission failure is returned to the dispatcher without a duplicate late error", async () => {
		const frames: RpcPromptResultFrame[] = [];
		const errors: Error[] = [];
		const results = new RpcPromptResults(idleSession, frame => frames.push(frame));
		const ack = watchAndReportPromptResult({
			ticket: results.begin("failure"),
			startPrompt: async () => { throw new Error("denied preflight"); },
			results,
			onError: error => errors.push(error),
			extensionUserMessageTracker: new RpcExtensionUserMessageTracker(),
		});
		await expect(ack).rejects.toThrow("denied preflight");
		results.acknowledge("failure");
		expect(frames).toEqual([]);
		expect(errors).toEqual([]);
	});
});
