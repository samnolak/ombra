// Completion check: when the main agent ends a turn, decide whether it stopped before the work was done
// (detect.ts) and, if so, continue the turn with one hidden reminder (nudge.md) naming the reason.
//
// The hook is `session_stop`: the engine fires it only for a main-agent turn that settles normally, never for
// subagents (they finish through `yield`), aborted turns, or turns that a retry, compaction or todo reminder already
// continues. Returning `continue` queues the context as a hidden agent-attributed message and runs one more turn,
// and the settling `agent_end` is marked non-terminal, so print/RPC callers wait for it.
//
// At most MAX_NUDGES per user message, and at most one per detected kind: a second identical nudge after the agent
// already answered the first would only repeat it.
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { detectPrematureStop, isUserMessage, type Msg, type Verdict } from "./detect.ts";

const MAX_NUDGES = 2;

export default function (pi: ExtensionAPI) {
	const template = readFileSync(join(import.meta.dir, "nudge.md"), "utf8").trim();
	const nudged = new Set<Verdict["kind"]>();

	pi.on("message_start", event => {
		if (isUserMessage(event.message as Msg)) nudged.clear();
	});

	pi.on("session_stop", (event, ctx) => {
		// A message the user queued meanwhile takes the next turn.
		if (nudged.size >= MAX_NUDGES || ctx.hasPendingMessages()) return;
		const verdict = detectPrematureStop(event.messages as Msg[]);
		if (!verdict || nudged.has(verdict.kind)) return;
		nudged.add(verdict.kind);
		return { continue: true, additionalContext: template.replace("{{reason}}", verdict.reason) };
	});
}
