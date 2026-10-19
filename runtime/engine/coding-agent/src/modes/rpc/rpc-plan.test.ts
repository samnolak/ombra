import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { realpathSync } from "node:fs";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import type { Context } from "@oh-my-pi/pi-ai";
import { createMockModel, type MockResponse, registerMockApi } from "@oh-my-pi/pi-ai/providers/mock";
import { isRecord } from "@oh-my-pi/pi-utils";
import { getAgentDir, setAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { Settings } from "../../config/settings";
import { withActiveSettings } from "../../config/settings-context";
import { AgentRegistry, createAgentSession } from "../../sdk";
import { SessionManager } from "../../session/session-manager";
import { cfgToolsApprovalMode } from "../../tools/settings";
import { createRpcSessionRuntime, type RpcSessionRuntime } from "./rpc-mode";

const PLAN = "# Demo plan\n\n1. Add the greeting.\n2. Test it.\n";
const REVISED = "# Demo plan\n\n1. Add the greeting.\n2. Test it.\n3. Document it.\n";

const TAMPERED = "# Demo plan\n\n1. Delete the repository.\n";

/**
 * The scripted model: drafts `local://demo-plan.md`, proposes it, then reacts to approval or feedback. "rewrite
 * quietly" rewrites the plan file without proposing it; "think long" streams for a long time (until aborted).
 */
function planningModel() {
	let drafted = PLAN;
	let quiet = false;
	const textOf = (message: Context["messages"][number]): string => {
		if (!("content" in message)) return "";
		const content: unknown = message.content;
		if (typeof content === "string") return content;
		if (!Array.isArray(content)) return "";
		return content.map(part => (isRecord(part) && typeof part.text === "string" ? part.text : "")).join("");
	};
	return createMockModel({
		id: "plan-mock",
		handler: (context): MockResponse => {
			const last = context.messages.at(-1);
			const text = last ? textOf(last) : "";
			if (last?.role !== "toolResult") {
				if (text.includes("Plan approved")) return { content: ["Executing the approved plan."] };
				if (text.includes("think long")) return { content: ["Still thinking."], delayMs: 30_000 };
				quiet = text.includes("rewrite quietly");
				if (quiet) return { content: [{ type: "toolCall", name: "write", arguments: { path: "local://demo-plan.md", content: TAMPERED } }] };
				if (text.includes("document it")) drafted = REVISED;
				return { content: [{ type: "toolCall", name: "write", arguments: { path: "local://demo-plan.md", content: drafted } }] };
			}
			if (quiet) return { content: ["Rewritten."] };
			if (text.includes("xd://propose") || text.toLowerCase().includes("plan ready")) {
				// Never reached in a review: the planning turn is stopped before the model answers.
				return { content: ["Waiting for approval."], delayMs: 10_000 };
			}
			return { content: [{ type: "toolCall", name: "write", arguments: { path: "xd://propose", content: "demo" } }] };
		},
	});
}

describe("RPC plan mode", () => {
	const root = path.join(realpathSync(os.tmpdir()), "ombra-tests", `run-${process.pid}`, "rpc-plan");
	const previousAgentDir = getAgentDir();
	beforeAll(async () => {
		await mkdir(path.join(root, "agent"), { recursive: true });
		setAgentDir(path.join(root, "agent"));
		registerMockApi();
	});
	afterAll(async () => {
		setAgentDir(previousAgentDir);
		await rm(root, { recursive: true, force: true });
	});

	async function open() {
		const cwd = await mkdtemp(path.join(root, "project-"));
		const sessionManager = SessionManager.create(cwd, path.join(cwd, ".sessions"));
		const settings = await Settings.loadIsolated({ cwd, agentDir: getAgentDir(), projectRoot: cwd, inMemory: true });
		cfgToolsApprovalMode.override(settings, "yolo");
		const model = planningModel();
		const registry = new AgentRegistry();
		const { session } = await AgentRegistry.runWithRegistry(registry, () =>
			withActiveSettings(settings, () =>
				createAgentSession({
					cwd, settings, sessionManager, agentRegistry: registry, model, bindProcessState: false,
					disableExtensionDiscovery: true, skills: [], rules: [], enableLsp: false,
				}),
			),
		);
		session.modelRegistry.authStorage.keys.setRuntime(model.provider, "mock-key");
		const frames: Record<string, unknown>[] = [];
		const waiters: Array<{ match: (frame: Record<string, unknown>) => boolean; resolve: (frame: Record<string, unknown>) => void }> = [];
		const runtime: RpcSessionRuntime = await withActiveSettings(settings, () =>
			createRpcSessionRuntime(session, {
				protocolVersion: 2,
				output: frame => {
					if (!isRecord(frame)) return;
					frames.push(frame);
					for (const waiter of [...waiters]) {
						if (!waiter.match(frame)) continue;
						waiters.splice(waiters.indexOf(waiter), 1);
						waiter.resolve(frame);
					}
				},
			}),
		);
		const next = (match: (frame: Record<string, unknown>) => boolean, after = frames.length): Promise<Record<string, unknown>> => {
			const seen = frames.slice(after).find(match);
			if (seen) return Promise.resolve(seen);
			const waiting = Promise.withResolvers<Record<string, unknown>>();
			waiters.push({ match, resolve: waiting.resolve });
			return waiting.promise;
		};
		let ids = 0;
		const request = async (command: Record<string, unknown>): Promise<Record<string, unknown>> => {
			const id = `r${++ids}`;
			const response = next(frame => frame.type === "response" && frame.id === id);
			runtime.dispatch({ ...command, id });
			return response;
		};
		const reviewed = (after: number) =>
			next(frame => frame.type === "plan_mode_updated" && isRecord(frame.review), after);
		return { session, runtime, frames, next, request, reviewed, model };
	}

	test("enter, review the proposed plan document, reject with feedback, approve and execute", async () => {
		const h = await open();
		try {
			const initial = await h.request({ type: "get_state" });
			expect((initial.data as Record<string, unknown>).planMode).toEqual({ state: null, review: null });

			let mark = h.frames.length;
			const entered = await h.request({ type: "plan_mode", op: "enter" });
			expect(entered).toMatchObject({ success: true, data: { state: { enabled: true, planFilePath: "local://PLAN.md" }, review: null } });
			expect(await h.next(frame => frame.type === "plan_mode_updated", mark)).toMatchObject({ state: { enabled: true }, review: null });
			expect(h.session.getEnabledToolNames()).toContain("write");

			// Approving before anything was proposed is refused.
			expect(await h.request({ type: "plan_mode", op: "approve" })).toMatchObject({ success: false, error: "No plan is awaiting review." });

			mark = h.frames.length;
			const prompted = await h.request({ type: "prompt", message: "Plan a greeting." });
			expect(prompted).toMatchObject({ success: true });
			const review = await h.reviewed(mark);
			expect(review.review).toMatchObject({ planFilePath: "local://demo-plan.md", title: "demo", content: PLAN });
			expect(typeof (review.review as Record<string, unknown>).revision).toBe("string");
			expect(review.state).toMatchObject({ enabled: true, planFilePath: "local://demo-plan.md" });
			// The planning turn stops quietly: the prompt completes, it is not reported as an abort.
			expect(await h.next(frame => frame.type === "prompt_result", mark)).toMatchObject({ status: "completed" });
			const reviewing = await h.request({ type: "get_state" });
			expect((reviewing.data as Record<string, unknown>).planMode).toEqual({ state: review.state, review: review.review });

			mark = h.frames.length;
			const rejected = await h.request({ type: "plan_mode", op: "reject", revision: (review.review as Record<string, unknown>).revision, feedback: "Also document it." });
			expect(rejected).toMatchObject({ success: true, data: { state: { enabled: true }, review: null } });
			const revised = await h.reviewed(mark);
			expect(revised.review).toMatchObject({ planFilePath: "local://demo-plan.md", content: REVISED });
			// The feedback went to the model as the next planning turn.
			expect(h.model.calls.some(call => JSON.stringify(call.context.messages).includes("Also document it."))).toBe(true);

			mark = h.frames.length;
			const approved = await h.request({ type: "plan_mode", op: "approve", revision: (revised.review as Record<string, unknown>).revision });
			expect(approved).toMatchObject({ success: true, data: { state: null, review: null } });
			await h.next(frame => frame.type === "agent_end", mark);
			const approvedTurn = h.model.calls.at(-1)?.context.messages ?? [];
			expect(JSON.stringify(approvedTurn)).toContain("Plan approved");
			expect(JSON.stringify(approvedTurn)).toContain("3. Document it.");
			expect(h.session.getPlanModeState()).toBeUndefined();
			const after = await h.request({ type: "get_state" });
			expect((after.data as Record<string, unknown>).planMode).toEqual({ state: null, review: null });
			expect(h.session.sessionManager.buildSessionContext().mode).not.toBe("plan");
		} finally {
			await h.runtime.close();
		}
	}, 60_000);

	test("approve and reject act only on the review shown, and approval runs the reviewed text", async () => {
		const h = await open();
		try {
			await h.request({ type: "plan_mode", op: "enter" });
			let mark = h.frames.length;
			await h.request({ type: "prompt", message: "Plan a greeting." });
			const first = (await h.reviewed(mark)).review as Record<string, unknown>;
			await h.next(frame => frame.type === "prompt_result", mark);

			// No revision, or a revision that is not the one waiting: refused, nothing changes.
			expect(await h.request({ type: "plan_mode", op: "approve" })).toMatchObject({ success: false, code: "plan_review_stale" });
			expect(await h.request({ type: "plan_mode", op: "reject", revision: "other" })).toMatchObject({ success: false, code: "plan_review_stale" });

			// Another planning turn proposes a new version: the card that showed the first one can no longer approve.
			mark = h.frames.length;
			await h.request({ type: "prompt", message: "Also document it." });
			const second = (await h.reviewed(mark)).review as Record<string, unknown>;
			await h.next(frame => frame.type === "prompt_result", mark);
			expect(second.content).toBe(REVISED);
			expect(second.revision).not.toBe(first.revision);
			expect(await h.request({ type: "plan_mode", op: "approve", revision: first.revision })).toMatchObject({ success: false, code: "plan_review_stale" });
			expect(h.session.getPlanModeState()?.enabled).toBe(true);

			// The agent rewrites the file after the review was shown: approval still runs exactly what was reviewed.
			mark = h.frames.length;
			await h.request({ type: "prompt", message: "rewrite quietly" });
			await h.next(frame => frame.type === "prompt_result", mark);
			const waiting = await h.request({ type: "get_state" });
			expect((waiting.data as { planMode: { review: Record<string, unknown> } }).planMode.review).toMatchObject({ revision: second.revision, content: REVISED });
			mark = h.frames.length;
			expect(await h.request({ type: "plan_mode", op: "approve", revision: second.revision })).toMatchObject({ success: true });
			await h.next(frame => frame.type === "agent_end", mark);
			const executed = JSON.stringify(h.model.calls.at(-1)?.context.messages.at(-1) ?? null);
			expect(executed).toContain("3. Document it.");
			expect(executed).not.toContain("Delete the repository");
		} finally {
			await h.runtime.close();
		}
	}, 60_000);

	test("approve and reject are refused while a planning turn runs; plan mode stays in force", async () => {
		const h = await open();
		try {
			const before = h.session.getEnabledToolNames();
			await h.request({ type: "plan_mode", op: "enter" });
			let mark = h.frames.length;
			await h.request({ type: "prompt", message: "Plan a greeting." });
			const review = (await h.reviewed(mark)).review as Record<string, unknown>;
			await h.next(frame => frame.type === "prompt_result", mark);
			const planTools = h.session.getEnabledToolNames();

			mark = h.frames.length;
			await h.request({ type: "prompt", message: "think long" });
			await h.next(frame => frame.type === "agent_start", mark);
			expect(h.session.isStreaming).toBe(true);
			for (const op of ["approve", "reject"]) {
				expect(await h.request({ type: "plan_mode", op, revision: review.revision })).toMatchObject({ success: false, code: "session_busy" });
			}
			expect(h.session.getPlanModeState()?.enabled).toBe(true);
			expect(h.session.getEnabledToolNames()).toEqual(planTools);
			const state = await h.request({ type: "get_state" });
			expect((state.data as { planMode: { review: unknown } }).planMode.review).toMatchObject({ revision: review.revision });

			// Once the turn is stopped the same review can be approved.
			await h.request({ type: "abort" });
			expect(await h.request({ type: "plan_mode", op: "approve", revision: review.revision })).toMatchObject({ success: true });
			expect(h.session.getPlanModeState()).toBeUndefined();
			expect(h.session.getEnabledToolNames()).toEqual(expect.arrayContaining(before));
		} finally {
			await h.runtime.close();
		}
	}, 60_000);

	test("exit leaves plan mode without approval and restores the tools", async () => {
		const h = await open();
		try {
			const before = h.session.getEnabledToolNames();
			await h.request({ type: "plan_mode", op: "enter", planFilePath: "local://draft-plan.md" });
			expect(h.session.sessionManager.buildSessionContext().mode).toBe("plan");
			const mark = h.frames.length;
			const exited = await h.request({ type: "plan_mode", op: "exit" });
			expect(exited).toMatchObject({ success: true, data: { state: null, review: null } });
			expect(await h.next(frame => frame.type === "plan_mode_updated", mark)).toMatchObject({ state: null, review: null });
			expect(h.session.getEnabledToolNames()).toEqual(before);
			expect(h.session.sessionManager.buildSessionContext().mode).toBe("none");
			expect(await h.request({ type: "plan_mode", op: "reject" })).toMatchObject({ success: false });
		} finally {
			await h.runtime.close();
		}
	}, 60_000);
});
