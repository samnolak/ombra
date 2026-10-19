/**
 * Prompt completion reporting for RPC mode.
 *
 * Every accepted `prompt`/`abort_and_prompt` that is not answered synchronously
 * with `data.agentInvoked: false` gets exactly one `prompt_result` frame, emitted
 * once all work the prompt caused has settled: immediately for local-only slash
 * commands and failures, or after the terminal `agent_end` of the run the prompt
 * started or joined. Hosts correlate on the command `id` instead of inferring
 * ownership of an `agent_end` that carries no prompt identity.
 */
import { AsyncLocalStorage } from "node:async_hooks";
import type { AgentMessage } from "@oh-my-pi/pi-agent-core";
import type { AssistantMessage } from "@oh-my-pi/pi-ai";
import * as AIError from "@oh-my-pi/pi-ai/error";
import { stripRawHttpRequestDiagnostics } from "@oh-my-pi/pi-ai/utils/http-inspector";
import type { AgentSessionEvent } from "../../session/agent-session";
import { isSilentAbort } from "../../session/messages";
import { isRpcSessionSettled, type RpcScheduledTurnProbe, type RpcSettleSession } from "./rpc-session-settle";
import type { RpcInputAck, RpcInputDisposition, RpcPromptError, RpcPromptResultFrame, RpcPromptStatus } from "./rpc-types";

/** A prompt accepted by RPC mode whose `prompt_result` is still owed; see {@link RpcPromptResults.begin}. */
export interface RpcPromptTicket {
	readonly id: string | undefined;
}

interface RunOutcome {
	status: RpcPromptStatus;
	error?: RpcPromptError;
}

interface OpenPrompt {
	/** `agent_start` count at acceptance; a later start begins a run this prompt may own. */
	startsAtBegin: number;
	/** Outcome of the first run started after acceptance, once the agent yielded it. */
	ownOutcome?: RunOutcome;
	/** Settled while another run was live: reported at the next yield that leaves nothing queued. */
	waiting: boolean;
	acknowledged: boolean;
	report?: { agentInvoked: boolean; outcome: RunOutcome };
	onFailure?: () => void;
}

/**
 * Correlates accepted prompts with the run that carries their work and emits
 * their `prompt_result`. Fed every session event through {@link observe}.
 *
 * A prompt reports when the agent **yields** its work (`agent_end` with
 * `yielded`), not when the session is done: background jobs may still wake the
 * session later (see {@link RpcSessionSettleWatcher}), which `sessionSettled`
 * on the frame reports.
 *
 * A prompt dispatched as a fresh turn owns the first run that starts after it
 * was accepted, so a late `agent_end` from an earlier run never settles it. A
 * prompt queued into a live run (steer/follow-up) resolves while that run
 * streams and reports at the first yield after its message left the queue.
 */
export class RpcPromptResults {
	#agentStarts = 0;
	/**
	 * The run's last assistant message was aborted silently (plan review stops the planning turn): control
	 * flow, not an operator abort. Read from `message_end`, where the session stamps the marker; the
	 * `agent_end` copy of the message does not carry it.
	 */
	#silentAbort = false;
	#open = new Map<RpcPromptTicket, OpenPrompt>();
	#awaitingAcknowledgement = new Set<RpcPromptTicket>();
	readonly #session: RpcSettleSession;
	readonly #output: (frame: RpcPromptResultFrame) => void;
	readonly #scheduledTurn: RpcScheduledTurnProbe | undefined;

	/**
	 * @param session read for queue state and, at report time, the `sessionSettled` predicate.
	 * @param scheduledTurn reports a host-scheduled turn not yet admitted (not settled).
	 */
	constructor(
		session: RpcSettleSession,
		output: (frame: RpcPromptResultFrame) => void,
		scheduledTurn?: RpcScheduledTurnProbe,
	) {
		this.#session = session;
		this.#output = output;
		this.#scheduledTurn = scheduledTurn;
	}

	/** Open a ticket before the prompt starts any work. Close it with exactly one report or {@link discard}. */
	begin(id: string | undefined): RpcPromptTicket {
		const ticket: RpcPromptTicket = { id };
		this.#open.set(ticket, { startsAtBegin: this.#agentStarts, waiting: false, acknowledged: false });
		this.#awaitingAcknowledgement.add(ticket);
		return ticket;
	}

	/** Drop a ticket whose command was rejected before it was accepted (no `prompt_result` is owed). */
	discard(ticket: RpcPromptTicket): void {
		this.#open.delete(ticket);
	}

	/** Called by the transport immediately after writing this input's response. */
	acknowledge(id: string | undefined): void {
		for (const ticket of this.#awaitingAcknowledgement) {
			if (ticket.id !== id) continue;
			this.#awaitingAcknowledgement.delete(ticket);
			const open = this.#open.get(ticket);
			if (open) {
				open.acknowledged = true;
				if (open.report) this.#report(ticket, open.report.agentInvoked, open.report.outcome);
			}
			return;
		}
	}

	/**
	 * The prompt's work reached the agent (dispatched or queued). Reports now if
	 * its own run already settled, otherwise at the next terminal `agent_end`.
	 */
	settle(ticket: RpcPromptTicket): void {
		const open = this.#open.get(ticket);
		if (!open) return;
		if (open.ownOutcome) {
			this.#report(ticket, true, open.ownOutcome);
		} else if (this.#session.isStreaming || this.#agentStarts > open.startsAtBegin) {
			// Queued into a live run, or its own run paused for agent-owned follow-up work (e.g. a retry).
			open.waiting = true;
		} else {
			// Idle with no run since acceptance: an abort won the race before dispatch.
			this.#report(ticket, true, { status: "aborted" });
		}
	}

	/** The prompt was handled locally without an agent turn. */
	completeLocal(ticket: RpcPromptTicket): void {
		this.#report(ticket, false, { status: "completed" });
	}

	/** Defer failure notification with the result until the input ACK has been written. */
	fail(ticket: RpcPromptTicket, message: string, onFailure?: () => void): void {
		const open = this.#open.get(ticket);
		if (!open || open.report) return;
		open.onFailure = onFailure;
		this.#report(ticket, false, { status: "error", error: { message, retryable: false } });
	}

	/**
	 * Mark every open prompt aborted after a session transition (host command or
	 * extension). Transitions detach the agent before aborting it, so the
	 * interrupted run never publishes a terminal `agent_end` for them.
	 */
	abortOpen(): void {
		for (const [ticket, open] of this.#open) {
			if (open.waiting) this.#report(ticket, true, { status: "aborted" });
			// A prompt with no run started since it was accepted was not detached: a
			// command still running (for example the extension command that made the
			// change) keeps its ticket for the run it starts in the new session.
			else if (this.#agentStarts > open.startsAtBegin) open.ownOutcome ??= { status: "aborted" };
		}
	}

	/** Track run boundaries; call after the event has been written so `prompt_result` follows its `agent_end`. */
	observe(event: AgentSessionEvent): void {
		if (event.type === "agent_start") {
			this.#agentStarts++;
			this.#silentAbort = false;
			return;
		}
		if (event.type === "message_end" && event.message.role === "assistant") {
			this.#silentAbort = event.message.stopReason === "aborted" && isSilentAbort(event.message);
			return;
		}
		if (event.type !== "agent_end" || this.#open.size === 0) return;
		// Older sessions omit `yielded`; only their terminal ends were yields.
		if (!(event.yielded ?? event.isTerminal !== false)) return;
		const ended = runOutcome(event.messages);
		const outcome: RunOutcome = ended.status === "aborted" && this.#silentAbort ? { status: "completed" } : ended;
		// A still-queued steer/follow-up has not been read by the agent yet.
		const queueDrained = this.#session.queuedMessageCount === 0;
		for (const [ticket, open] of this.#open) {
			if (open.waiting) {
				if (queueDrained) this.#report(ticket, true, outcome);
			} else if (!open.ownOutcome && this.#agentStarts > open.startsAtBegin) {
				open.ownOutcome = outcome;
			}
		}
	}

	#report(ticket: RpcPromptTicket, agentInvoked: boolean, outcome: RunOutcome): void {
		const open = this.#open.get(ticket);
		if (!open || (open.report && !open.acknowledged)) return;
		if (!open.acknowledged) {
			open.report = { agentInvoked, outcome };
			return;
		}
		this.#open.delete(ticket);
		const frame: RpcPromptResultFrame = {
			type: "prompt_result",
			id: ticket.id,
			agentInvoked,
			status: outcome.status,
			sessionSettled: isRpcSessionSettled(this.#session, this.#scheduledTurn),
		};
		if (outcome.error) frame.error = outcome.error;
		open.onFailure?.();
		this.#output(frame);
	}
}

/** Outcome of a run, read from its final assistant message. */
function runOutcome(messages: readonly AgentMessage[]): RunOutcome {
	for (let index = messages.length - 1; index >= 0; index--) {
		const message = messages[index];
		if (message.role !== "assistant") continue;
		if (message.stopReason === "error") return { status: "error", error: promptError(message) };
		if (message.stopReason === "aborted") return { status: "aborted" };
		return { status: "completed" };
	}
	return { status: "completed" };
}

function promptError(message: AssistantMessage): RpcPromptError {
	const errorId = AIError.classifyMessage({
		api: message.api,
		provider: message.provider,
		model: message.model,
		errorId: message.errorId,
		errorMessage: message.errorMessage,
		errorClassificationMessage: message.errorClassificationMessage,
		errorStatus: message.errorStatus,
	});
	const error: RpcPromptError = {
		message: message.errorMessage ? stripRawHttpRequestDiagnostics(message.errorMessage) : "Provider request failed",
		provider: message.provider,
		model: message.model,
		retryable: AIError.is(errorId, AIError.Flag.Transient),
	};
	if (message.errorStatus !== undefined) error.httpStatus = message.errorStatus;
	return error;
}

interface RpcExtensionUserMessageScope {
	parent: RpcExtensionUserMessageScope | undefined;
	hasAgentMessageTask: boolean;
	hasCancelledAgentMessage: boolean;
	promptSettled: boolean;
	pendingAgentMessageTasks: Set<Promise<void>>;
	pendingAdmissions: Set<Promise<void>>;
	admissionError?: { cause: unknown };
}

/**
 * Tracks only sends causally descended from an input, including sends started
 * by another send's asynchronous extension hooks. Concurrent inputs never
 * inherit each other's tasks. Admission and full-turn completion are separate.
 */
export class RpcExtensionUserMessageTracker {
	readonly #scope = new AsyncLocalStorage<RpcExtensionUserMessageScope>();

	/** Release this session's async context after its outstanding work has drained. */
	dispose(): void {
		this.#scope.disable();
	}

	trackAgentMessageTask(task: Promise<unknown>, admission: Promise<RpcInputDisposition>): void {
		for (let scope = this.#scope.getStore(); scope; scope = scope.parent) {
			if (scope.promptSettled && scope.pendingAgentMessageTasks.size === 0) continue;
			const current = scope;
			const pendingAdmission = admission.then(
				disposition => {
					if (disposition === "admitted") current.hasAgentMessageTask = true;
					else if (disposition === "cancelled") current.hasCancelledAgentMessage = true;
				},
				cause => {
					current.admissionError ??= { cause };
				},
			);
			current.pendingAdmissions.add(pendingAdmission);
			void pendingAdmission.then(() => current.pendingAdmissions.delete(pendingAdmission));
			const pendingTask = task.then(
				() => {},
				() => {},
			);
			current.pendingAgentMessageTasks.add(pendingTask);
			void pendingTask.then(() => current.pendingAgentMessageTasks.delete(pendingTask));
		}
	}

	watchPrompt<T>(startPrompt: () => Promise<T>): {
		prompt: Promise<T>;
		hasAgentMessageTask: () => boolean;
		hasCancelledAgentMessage: () => boolean;
		waitForAgentMessageTasks: () => Promise<void>;
		waitForAgentMessageAdmissions: () => Promise<void>;
	} {
		const scope: RpcExtensionUserMessageScope = {
			parent: this.#scope.getStore(),
			hasAgentMessageTask: false,
			hasCancelledAgentMessage: false,
			promptSettled: false,
			pendingAgentMessageTasks: new Set(),
			pendingAdmissions: new Set(),
		};
		let prompt: Promise<T>;
		try {
			prompt = this.#scope.run(scope, startPrompt);
		} catch (error) {
			scope.promptSettled = true;
			throw error;
		}
		return {
			prompt: prompt.finally(() => {
				scope.promptSettled = true;
			}),
			hasAgentMessageTask: () => scope.hasAgentMessageTask,
			hasCancelledAgentMessage: () => scope.hasCancelledAgentMessage,
			waitForAgentMessageTasks: async () => {
				while (scope.pendingAgentMessageTasks.size > 0) {
					await Promise.all(scope.pendingAgentMessageTasks);
				}
			},
			waitForAgentMessageAdmissions: async () => {
				while (scope.pendingAdmissions.size > 0) {
					await Promise.all(scope.pendingAdmissions);
				}
				if (scope.admissionError && !scope.hasAgentMessageTask) throw scope.admissionError.cause;
			},
		};
	}
}

/** Report completion independently of the input acknowledgement barrier. */
export function reportPromptResult(input: {
	ticket: RpcPromptTicket;
	prompt: Promise<boolean>;
	results: RpcPromptResults;
	onError: (error: Error) => void;
	hasExtensionAgentMessageTask?: () => boolean;
	waitForExtensionAgentMessageTasks?: () => Promise<void>;
	wasAdmitted?: () => boolean;
}): void {
	void input.prompt
		.then(async agentInvoked => {
			await input.waitForExtensionAgentMessageTasks?.();
			if (agentInvoked || input.hasExtensionAgentMessageTask?.()) input.results.settle(input.ticket);
			else input.results.discard(input.ticket);
		})
		.catch(cause => {
			if (input.wasAdmitted && !input.wasAdmitted()) {
				input.results.discard(input.ticket);
				return;
			}
			const error = cause instanceof Error ? cause : new Error(String(cause));
			input.results.fail(input.ticket, error.message, () => input.onError(error));
		});
}

/**
 * Observe the whole input path, including input hooks and locally handled
 * commands. Direct admission can release an in-flight prompt. Local paths
 * wait for their hooks/handlers and nested admissions, never nested full turns.
 */
export async function watchRpcInputAdmission(input: {
	startInput: (onPromptAdmitted: (inputDisposition?: RpcInputDisposition) => void) => Promise<RpcInputDisposition>;
	extensionUserMessageTracker: RpcExtensionUserMessageTracker;
	report?: {
		ticket: RpcPromptTicket;
		results: RpcPromptResults;
		onError: (error: Error) => void;
	};
}): Promise<RpcInputAck> {
	const ready = Promise.withResolvers<void>();
	let directDisposition: RpcInputDisposition | undefined;
	const tracked = input.extensionUserMessageTracker.watchPrompt(() =>
		input.startInput((disposition = "admitted") => {
			directDisposition = disposition;
			ready.resolve();
		}),
	);
	if (input.report) {
		reportPromptResult({
			...input.report,
			prompt: tracked.prompt.then(() => directDisposition !== undefined),
			hasExtensionAgentMessageTask: tracked.hasAgentMessageTask,
			waitForExtensionAgentMessageTasks: tracked.waitForAgentMessageTasks,
			wasAdmitted: () => directDisposition !== undefined || tracked.hasAgentMessageTask(),
		});
	}
	void tracked.prompt.then(
		() => ready.resolve(),
		() => ready.resolve(),
	);
	await ready.promise;
	try {
		await tracked.waitForAgentMessageAdmissions();
	} catch (error) {
		// A sibling extension failure cannot withdraw input already delivered.
		// Its own extension_send error still reports through the runtime.
		if (directDisposition === undefined) throw error;
	}
	if (directDisposition !== undefined) {
		return { inputDisposition: directDisposition, agentInvoked: true };
	}
	const disposition = await tracked.prompt;
	const agentInvoked = tracked.hasAgentMessageTask();
	// A resolved SDK call without its admission callback is not proof of delivery.
	return {
		inputDisposition:
			(!agentInvoked && tracked.hasCancelledAgentMessage()) || disposition === "admitted" ? "cancelled" : disposition,
		agentInvoked,
	};
}

/** Start a full-turn prompt while acknowledging only its causal admission. */
export function watchAndReportPromptResult(input: {
	ticket: RpcPromptTicket;
	startPrompt: (onPromptAdmitted: () => void) => Promise<boolean>;
	results: RpcPromptResults;
	onError: (error: Error) => void;
	extensionUserMessageTracker: RpcExtensionUserMessageTracker;
}): Promise<RpcInputAck> {
	return watchRpcInputAdmission({
		startInput: async onPromptAdmitted => ((await input.startPrompt(onPromptAdmitted)) ? "cancelled" : "local"),
		extensionUserMessageTracker: input.extensionUserMessageTracker,
		report: { ticket: input.ticket, results: input.results, onError: input.onError },
	});
}
