/**
 * Plan mode for RPC hosts (`--mode rpc` and `--mode rpc-ui`).
 *
 * The session owns plan-mode state, the plan-mode system message and the
 * `xd://propose` validation (`AgentSession.preparePlanForReview`). This
 * controller adds what `InteractiveMode` otherwise owns: the tool set while
 * planning, the review of a proposed plan, approval (exit plan mode, then run
 * the plan) and refinement (feedback sent as the next planning turn), and
 * reattach after a session change. Every transition is pushed to the host as a
 * `plan_mode_updated` frame carrying the plan document under review.
 */
import { logger, prompt } from "@oh-my-pi/pi-utils";
import { PROPOSE_DEVICE_NAME } from "@oh-my-pi/pi-tui/tools/resolve";
import { humanizePlanTitle, type PlanApprovalDetails } from "../../plan-mode/approved-plan";
import { autosaveApprovedPlan } from "../../plan-mode/plan-autosave";
import { readPlanFile, resolvePlanFilePath } from "../../plan-mode/plan-files";
import { cfgPlanEnabled } from "../../plan-mode/settings";
import type { PlanModeState } from "../../plan-mode/state";
import planModeApprovedPrompt from "../../prompts/system/plan-mode-approved.md" with { type: "text" };
import type { AgentSession, AgentSessionEvent } from "../../session/agent-session";
import { USER_INTERRUPT_LABEL } from "../../session/messages";
import { isMCPToolName } from "../../tools/builtin-names";
import { writeDeviceDispatch } from "../../tools/resolve";

/** Default plan document, as in the TUI and ACP. */
export const RPC_DEFAULT_PLAN_FILE = "local://PLAN.md";

export type RpcPlanModeOp = "get" | "enter" | "exit" | "approve" | "reject";

export interface RpcPlanModeCommand {
	op: RpcPlanModeOp;
	/** `enter`: plan document to draft; defaults to the session's plan reference (`local://PLAN.md`). */
	planFilePath?: string;
	/** `reject`: what to change; sent as the next planning turn. Empty dismisses the review and keeps planning. */
	feedback?: string;
	/** `approve`/`reject`: the {@link RpcPlanReview.revision} the host showed; any other is refused (`plan_review_stale`). */
	revision?: string;
}

/** A plan the agent proposed (`xd://propose`) and the host has not yet approved or rejected. */
export interface RpcPlanReview {
	/** Opaque id of this proposal; a new proposal gets a new one. Approve/reject name it. */
	revision: string;
	planFilePath: string;
	title: string;
	/** The plan document (Markdown) as it was when proposed; approval executes exactly this text. */
	content: string;
}

/** A refused `plan_mode` op; `code` goes on the RPC error response. */
export class RpcPlanModeError extends Error {
	constructor(
		message: string,
		readonly code: "plan_review_stale" | "session_busy",
	) {
		super(message);
		this.name = "RpcPlanModeError";
	}
}

export interface RpcPlanModeResult {
	/** `null` when plan mode is off. */
	state: PlanModeState | null;
	/** `null` when no proposed plan waits for the host. */
	review: RpcPlanReview | null;
}

export interface RpcPlanModeUpdatedFrame extends RpcPlanModeResult {
	type: "plan_mode_updated";
}

export type RpcPlanSession = Pick<
	AgentSession,
	| "settings"
	| "sessionManager"
	| "getPlanModeState"
	| "setPlanModeState"
	| "getGoalModeState"
	| "getVibeModeState"
	| "getPlanReferencePath"
	| "setPlanReferencePath"
	| "markPlanReferenceSent"
	| "setPlanProposalHandler"
	| "preparePlanForReview"
	| "getEnabledToolNames"
	| "getMountedXdevToolNames"
	| "setActiveToolsByName"
	| "restoreNonMCPToolPresentation"
	| "hasBuiltInTool"
	| "sendPlanModeContext"
	| "markPlanInternalAbortPending"
	| "clearPlanInternalAbortPending"
	| "runModeExitTeardown"
	| "abort"
	| "prompt"
	| "isStreaming"
>;

interface ToolPresentation {
	enabled: string[];
	mounted: string[];
}

export class RpcPlanController {
	readonly #session: RpcPlanSession;
	readonly #output: (frame: RpcPlanModeUpdatedFrame) => void;
	/** Plan mode entered by this controller (host command or reattach); only then are proposals reviewed here. */
	#active = false;
	/** Non-MCP tool presentation before plan mode added `write`; restored on exit. */
	#previousPresentation: ToolPresentation | undefined;
	#review: RpcPlanReview | undefined;
	#reviewSeq = 0;
	/** Plan mode was entered before in this session: the plan-mode prompt says so. */
	#hasEntered = false;
	/** Reviews being opened and transitions run one at a time; commands and reads wait for them. */
	#task: Promise<void> = Promise.resolve();

	constructor(session: RpcPlanSession, output: (frame: RpcPlanModeUpdatedFrame) => void) {
		this.#session = session;
		this.#output = output;
	}

	get snapshot(): RpcPlanModeResult {
		const state = this.#session.getPlanModeState();
		return { state: state?.enabled ? { ...state } : null, review: this.#review ? { ...this.#review } : null };
	}

	/** Resolves once queued review openings and reattaches have run. */
	async settled(): Promise<void> {
		for (let tail = this.#task; ; tail = this.#task) {
			await tail;
			if (tail === this.#task) return;
		}
	}

	async handle(command: RpcPlanModeCommand): Promise<RpcPlanModeResult> {
		await this.settled();
		switch (command.op) {
			case "get":
				return this.snapshot;
			case "enter":
				await this.#enter({ planFilePath: command.planFilePath, persist: true });
				return this.snapshot;
			case "exit":
				await this.#exit();
				return this.snapshot;
			case "approve":
				await this.#approve(this.#reviewNamed(command.revision));
				return this.snapshot;
			case "reject":
				await this.#reject(this.#reviewNamed(command.revision), command.feedback);
				return this.snapshot;
			default: {
				const op: never = command.op;
				throw new Error(`Unknown plan_mode op: ${String(op)}`);
			}
		}
	}

	/**
	 * Feed session events. A successful `write` to `xd://propose` while this
	 * controller's plan mode is active opens the review: the planning turn stops
	 * (silently, as in the TUI, so the model cannot re-propose in a loop) and the
	 * plan document goes to the host.
	 */
	observe(event: AgentSessionEvent): void {
		if (event.type !== "tool_execution_end" || event.isError || !this.#active) return;
		const dispatch = writeDeviceDispatch(event.toolName, event.result);
		const details = dispatch?.tool === PROPOSE_DEVICE_NAME && dispatch.mode === "execute" ? dispatch.inner : undefined;
		if (!isPlanApprovalDetails(details)) return;
		this.#queue(() => this.#openReview(details));
	}

	/**
	 * Leave the previous transcript's plan mode behind and restore plan mode
	 * journaled in the current one (startup, new/switch/branch/fork/open), as
	 * the TUI reattaches it. Never throws.
	 */
	async reconcile(): Promise<void> {
		const run = this.#task.then(() => this.#reconcileOnce());
		this.#task = run.catch(reportControllerError);
		await this.#task;
	}

	#queue(work: () => Promise<void>): void {
		this.#task = this.#task.then(work).catch(reportControllerError);
	}

	#emit(): void {
		this.#output({ type: "plan_mode_updated", ...this.snapshot });
	}

	async #reconcileOnce(): Promise<void> {
		const wasVisible = this.#active || this.#review !== undefined;
		// Plan state belongs to the transcript that set it; the session keeps it across a switch.
		if (this.#active) await this.#clear();
		const context = this.#session.sessionManager.buildSessionContext();
		if (context.mode !== "plan") {
			if (wasVisible) this.#emit();
			return;
		}
		if (!cfgPlanEnabled.get(this.#session.settings)) {
			// As in the TUI: a disabled setting drops the stale mode instead of restoring it later.
			this.#session.sessionManager.appendModeChange("none");
			if (wasVisible) this.#emit();
			return;
		}
		const planFilePath = context.modeData?.planFilePath;
		await this.#enter({ planFilePath: typeof planFilePath === "string" ? planFilePath : undefined, persist: false });
	}

	#assertCanEnter(): void {
		if (!cfgPlanEnabled.get(this.#session.settings)) {
			throw new Error("Plan mode is disabled (plan.enabled).");
		}
		const goal = this.#session.getGoalModeState();
		if (goal?.enabled || goal?.goal.status === "paused") throw new Error("Exit goal mode first.");
		if (this.#session.getVibeModeState()?.enabled) throw new Error("Exit vibe mode first.");
	}

	async #enter(options: { planFilePath?: string; persist: boolean }): Promise<void> {
		if (this.#active) return;
		this.#assertCanEnter();
		const session = this.#session;
		const planFilePath = options.planFilePath?.trim() || session.getPlanReferencePath() || RPC_DEFAULT_PLAN_FILE;
		const previousTools = session.getEnabledToolNames();
		const previousMounted = session.getMountedXdevToolNames();
		// The plan-mode prompt drafts the plan with `write` and submits it with a `write` to
		// `xd://propose`; only the built-in `write` keeps plan mode's read-only guard.
		const planTools = session.hasBuiltInTool("write") ? [...new Set([...previousTools, "write"])] : previousTools;
		const previousState = session.getPlanModeState();
		// State first: under Code Mode the tool partition reads it.
		session.setPlanModeState({ enabled: true, planFilePath, workflow: "parallel", reentry: this.#hasEntered });
		try {
			await session.setActiveToolsByName(planTools);
		} catch (error) {
			session.setPlanModeState(previousState);
			throw error;
		}
		this.#previousPresentation = {
			enabled: previousTools.filter(name => !isMCPToolName(name)),
			mounted: previousMounted.filter(name => !isMCPToolName(name)),
		};
		this.#active = true;
		this.#hasEntered = true;
		session.setPlanProposalHandler(title => session.preparePlanForReview(title));
		if (session.isStreaming) await session.sendPlanModeContext({ deliverAs: "steer" });
		if (options.persist) session.sessionManager.appendModeChange("plan", { planFilePath });
		this.#emit();
	}

	/** Host exit without approval. A planning turn in flight stops: its prompt keeps planning until a proposal. */
	async #exit(): Promise<void> {
		if (!this.#active) return;
		const teardown = async (): Promise<void> => {
			await this.#clear();
			this.#session.sessionManager.appendModeChange("none");
		};
		if (this.#session.isStreaming) {
			await this.#session.runModeExitTeardown(async () => {
				await this.#session.abort({ reason: USER_INTERRUPT_LABEL });
				await teardown();
			});
		} else {
			await teardown();
		}
		this.#emit();
	}

	/** Drop plan mode and restore the pre-plan tools (or `presentation`). Not journaled. */
	async #clear(presentation?: ToolPresentation): Promise<void> {
		const session = this.#session;
		const state = session.getPlanModeState();
		session.setPlanModeState(undefined);
		const restore = presentation ?? this.#previousPresentation;
		try {
			if (restore) await session.restoreNonMCPToolPresentation(restore.enabled, restore.mounted);
		} catch (error) {
			session.setPlanModeState(state);
			throw error;
		}
		session.setPlanProposalHandler(null);
		this.#active = false;
		this.#previousPresentation = undefined;
		this.#review = undefined;
	}

	async #openReview(details: PlanApprovalDetails): Promise<void> {
		const session = this.#session;
		const state = session.getPlanModeState();
		if (!this.#active || !state?.enabled) return;
		session.markPlanInternalAbortPending();
		try {
			await session.abort();
		} finally {
			session.clearPlanInternalAbortPending();
		}
		if (!this.#active) return;
		const planFilePath = details.planFilePath || state.planFilePath;
		const content = await this.#readPlan(planFilePath);
		if (content === null) {
			logger.warn("RPC plan review: proposed plan file is missing", { planFilePath });
			return;
		}
		// The next planning turn (refinement) must target the plan just reviewed.
		const current = session.getPlanModeState();
		if (current?.enabled && current.planFilePath !== planFilePath) {
			session.setPlanModeState({ ...current, planFilePath });
			session.sessionManager.appendModeChange("plan", { planFilePath });
		}
		this.#review = { revision: `${session.sessionManager.getSessionId()}:${++this.#reviewSeq}`, planFilePath, title: details.title, content };
		this.#emit();
	}

	/**
	 * The review the host named, while nothing runs. A planning turn still streaming may propose another version or
	 * act under plan mode's guard: approving (which restores the full tools) or rejecting meanwhile is refused.
	 */
	#reviewNamed(revision: string | undefined): RpcPlanReview {
		const review = this.#review;
		if (!this.#active || !review) throw new RpcPlanModeError("No plan is awaiting review.", "plan_review_stale");
		if (revision !== review.revision) {
			throw new RpcPlanModeError("This plan is no longer the one awaiting review; review the current plan.", "plan_review_stale");
		}
		if (this.#session.isStreaming) {
			throw new RpcPlanModeError("omp is still working in plan mode; stop it or wait before approving or rejecting the plan.", "session_busy");
		}
		return review;
	}

	async #approve(review: RpcPlanReview): Promise<void> {
		const session = this.#session;
		// Exactly the text the host reviewed: a later rewrite of the file is not what was approved.
		const planContent = review.content;
		// The approved prompt names the file as the plan's durable copy: make it the reviewed text.
		if ((await this.#readPlan(review.planFilePath)) !== planContent) {
			await Bun.write(resolvePlanFilePath(review.planFilePath, this.#planPathOptions()), planContent);
		}
		const previous = this.#previousPresentation ?? {
			enabled: session.getEnabledToolNames().filter(name => !isMCPToolName(name)),
			mounted: session.getMountedXdevToolNames().filter(name => !isMCPToolName(name)),
		};
		// Execution keeps `read` so the durable plan copy stays reachable if the inline one is lost.
		const execution = previous.enabled.includes("read") ? previous : { ...previous, enabled: [...previous.enabled, "read"] };
		await this.#clear(execution);
		session.sessionManager.appendModeChange("none");
		session.setPlanReferencePath(review.planFilePath);
		try {
			await autosaveApprovedPlan({
				settings: session.settings,
				cwd: session.sessionManager.getCwd(),
				title: review.title,
				planContent,
			});
		} catch (error) {
			logger.warn("RPC plan approval: autosave failed", { error: error instanceof Error ? error.message : String(error) });
		}
		const seededName = humanizePlanTitle(review.title);
		if (seededName && !session.sessionManager.getSessionName()) {
			await session.sessionManager.setSessionName(seededName, "auto");
		}
		this.#emit();
		// The approved-plan prompt carries the plan; it is the reference injection.
		session.markPlanReferenceSent();
		const text = prompt.render(planModeApprovedPrompt, {
			planFilePath: review.planFilePath,
			planContent,
			contextPreserved: true,
		});
		await this.#dispatch(text, true);
	}

	async #reject(review: RpcPlanReview, feedback: string | undefined): Promise<void> {
		if (this.#review !== review) return;
		this.#review = undefined;
		this.#emit();
		const refinement = feedback?.trim();
		if (refinement) await this.#dispatch(refinement, false);
	}

	/** Start (or queue behind a running turn) the next turn; resolves once it is admitted, not when it ends. */
	async #dispatch(text: string, synthetic: boolean): Promise<void> {
		const admitted = Promise.withResolvers<void>();
		const run = this.#session.prompt(text, {
			synthetic,
			streamingBehavior: "followUp",
			onPromptAdmitted: () => admitted.resolve(),
		});
		run.then(
			() => admitted.resolve(),
			error => {
				admitted.reject(error);
				reportControllerError(error);
			},
		);
		await admitted.promise;
	}

	#planPathOptions() {
		const sessionManager = this.#session.sessionManager;
		return {
			localProtocolOptions: {
				getArtifactsDir: () => sessionManager.getArtifactsDir(),
				getSessionId: () => sessionManager.getSessionId(),
			},
			cwd: sessionManager.getCwd(),
		};
	}

	async #readPlan(planFilePath: string): Promise<string | null> {
		return readPlanFile(planFilePath, this.#planPathOptions());
	}
}

function isPlanApprovalDetails(value: unknown): value is PlanApprovalDetails {
	return (
		typeof value === "object" &&
		value !== null &&
		"planFilePath" in value &&
		"title" in value &&
		"planExists" in value &&
		typeof value.planFilePath === "string" &&
		typeof value.title === "string" &&
		typeof value.planExists === "boolean"
	);
}

function reportControllerError(error: unknown): void {
	logger.warn("RPC plan controller failed", { error: error instanceof Error ? error.message : String(error) });
}
