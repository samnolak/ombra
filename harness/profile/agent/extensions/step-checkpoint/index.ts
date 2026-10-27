// Step checkpoint: a soft cap on tool calls per user turn. omp's loop guard only catches identical repeated calls;
// a small model can still wander through hundreds of different calls without checking in with the user.
// Every `threshold` counted tool calls since the last user message, a hidden message (checkpoint.md) asks the model
// to stop, report what is done, left and blocking, and ask the user whether to continue. The turn is never killed.
// Each session (main agent and every subagent) binds this factory separately and counts only its own calls.
//
// Settings: config.json next to this file; OMP_STEP_CHECKPOINT=<n> overrides `threshold`; 0 disables.
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { readFileSync } from "node:fs";
import { join } from "node:path";

interface Config {
	threshold: number;
	exempt: string[];
}

const CUSTOM_TYPE = "step-checkpoint";

function loadConfig(): { threshold: number; exempt: Set<string> } {
	const cfg: Config = JSON.parse(readFileSync(join(import.meta.dir, "config.json"), "utf8"));
	const env = process.env.OMP_STEP_CHECKPOINT?.trim();
	const threshold = env ? Number(env) : cfg.threshold;
	if (!Number.isInteger(threshold) || threshold < 0) {
		throw new Error(
			`step-checkpoint: threshold must be a non-negative integer, got ${env ? `OMP_STEP_CHECKPOINT=${env}` : JSON.stringify(cfg.threshold)}`,
		);
	}
	return { threshold, exempt: new Set(cfg.exempt) };
}

// `{{#if subagent}}A{{else}}B{{/if}}` picks the variant for the session's agent kind; `{{count}}` is the call count.
function render(template: string, count: number, subagent: boolean): string {
	return template
		.replace(/\{\{#if subagent\}\}\n?([\s\S]*?)\{\{else\}\}\n?([\s\S]*?)\{\{\/if\}\}\n?/g, (_, sub: string, main: string) =>
			subagent ? sub : main,
		)
		.replaceAll("{{count}}", String(count))
		.trim();
}

export default function (pi: ExtensionAPI) {
	const { threshold, exempt } = loadConfig();
	if (threshold === 0) return;
	const template = readFileSync(join(import.meta.dir, "checkpoint.md"), "utf8");
	let calls = 0;

	// A user message starts a new count. Agent-attributed user messages (subagent budget and async-pending notices)
	// are engine notices, not the user, and do not reset it.
	pi.on("message_start", event => {
		const message = event.message;
		if (message.role === "user" && message.attribution !== "agent") calls = 0;
	});

	// `tool_execution_start` comes from this session's own agent loop only: subagents and advisors run their own
	// loops, so their calls never reach this counter.
	pi.on("tool_execution_start", (event, ctx) => {
		if (exempt.has(event.toolName)) return;
		calls++;
		// Idle means the turn already ended (e.g. aborted); an aside would then start a new turn by itself.
		if (calls % threshold !== 0 || ctx.isIdle()) return;
		pi.sendMessage(
			{
				customType: CUSTOM_TYPE,
				content: render(template, calls, ctx.agent.kind === "sub"),
				display: false,
				attribution: "agent",
				details: { calls, threshold },
			},
			// Lands at the next step boundary without interrupting the in-flight tool batch.
			{ deliverAs: "aside" },
		);
	});
}
