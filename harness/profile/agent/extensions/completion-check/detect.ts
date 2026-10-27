// Premature-stop detection over the messages of one user turn. Pure and dependency-free: `index.ts` calls it at
// turn end, and offline analysis can run it over recorded transcripts.

interface Part {
	type: string;
	text?: string;
	id?: string;
	name?: string;
	arguments?: Record<string, unknown>;
}

/** Structural subset of the engine's AgentMessage that detection reads. */
export interface Msg {
	role: string;
	content?: string | Part[];
	attribution?: string;
	stopReason?: string;
	toolCallId?: string;
	toolName?: string;
	isError?: boolean;
	details?: { exitCode?: number | null } & Record<string, unknown>;
}

export interface Verdict {
	kind: "unfinished-admission" | "failed-check" | "no-edit";
	reason: string;
}

const EDIT_TOOLS: Record<string, true> = { edit: true, write: true, ast_edit: true, apply_patch: true };

// (a) The final text says the work is not done. Each pattern names unfinished work in the first person or the
// work's state; summaries of finished work ("all tests pass", "no longer fails") do not match.
const UNFINISHED: RegExp[] = [
	/\bI (?:have not|haven['’]t|did not|didn['’]t|was not able to|wasn['’]t able to|could not|couldn['’]t) (?:yet )?(?:made|make|applied|apply|implemented|implement|changed|change|completed?|finish(?:ed)?|fix(?:ed)?)\b/i,
	/\b(?:is|are|was|were|remains?)(?: still)? not (?:yet )?(?:complete|completed|finished|done|ready)\b|\b(?:isn['’]t|aren['’]t) (?:yet )?(?:complete|finished|done|ready)\b/i,
	/\b(?:is|remains|was) (?:still |currently )?(?:incomplete|unresolved|unfinished)\b/i,
	/\b(?:is|are|remains?) (?:still |currently )?broken\b/i,
	/\b(?:has|have) not (?:yet )?been (?:re-?run|run|tested|verified)\b|\b(?:hasn['’]t|haven['’]t) (?:yet )?been (?:re-?run|run|tested|verified)\b/i,
	/\bstill (?:fails?|failing|broken|errors?|raises|need(?:s)? to)\b/i,
	/\b(?:can['’]t|cannot) (?:claim|deliver)\b/i,
	/\bnot run [^.\n]{0,40}\byet\b/i,
	/\bnext,? I(?:['’]ll| will)\b/i,
	/\bI(?:['’]ll| will) now\b/i,
	/\blet me now\b/i,
	/\bI haven['’]t [^.\n]{0,30}\byet\b/i,
	/\bI need to (?:add|change|fix|implement|update|make|edit|apply)\b/i,
	/ещё не|еще не/i,
	/не заверш/i,
	/не доделан/i,
	/(?:всё|все) ещё (?:пада|не работ|ошиб)/i,
	/не удалось (?:исправ|заверш|доделать|реализ)/i,
];

// The final text asks the user something; the turn is a hand-off, not a stop.
const QUESTION =
	/\?(?=[\s*_)"'`»]*(?:$|\n))|\b(?:should I|shall I|do you want|would you like|let me know|please confirm)\b|хотите ли|нужно ли|подтвердите|дайте знать/im;

// A blocker only the user can clear.
const USER_BLOCKER =
	/\b(?:permission|credentials?|api key|access token|password|log ?in|sign in|authenticat\w*|approval|need(?:s)? (?:you|your)|please (?:provide|share|confirm|grant|run)|requires? your|waiting (?:for|on) you|blocked (?:on|by) (?:you|missing|access))\b|нужен доступ|нет доступа|разрешени|учётн|учетн|пароль|ключ api|нужно ваше|от вас/i;

// A request for a change, unless it is phrased as a question.
const CHANGE_REQUEST =
	/\b(?:fix|implement|add|change|update|modify|make|refactor|rename|remove|delete|create|write|resolve|replace|support|migrate|convert|edit)\b|исправ|почин|добав|реализ|сделай|измени|обнови|удали|перепиши|напиши|создай|замени|поправ/i;
const QUESTION_REQUEST =
	/^\s*(?:what|why|how|where|when|which|who|is|are|does|do|can|could|explain|describe|review|summari[sz]e|show|list|find|check|investigate|analy[sz]e|compare|что|как|почему|зачем|где|какой|какие|объясни|расскажи|покажи|проверь|посмотри|найди)\b/i;

// Non-zero exits that mean "no match" or "differs", not failure.
const BENIGN_EXIT_1 = /^\s*(?:grep|egrep|fgrep|rg|diff|cmp|test|\[|git (?:diff|grep)|which|command -v)\b/;
// Shell commands that change files.
const SHELL_EDIT =
	/\bsed\s+(?:-\w*\s+)*-i|\bperl\s+-\w*i|\btee\b|\bgit\s+(?:apply|checkout|restore|stash|am|mv|rm)\b|\bpatch\b|\bapply_patch\b|(?:^|[^0-9&>])>{1,2}\s*(?!&|\/dev\/)[\w./~"'-]/;

// `edit.run`: an edit result that ran its check ends with "Run `<command>`:" and the bash output; a failed exit
// carries bash's "Command exited with code N" notice. A skipped run ("Run `…` skipped: …") ran nothing.
const EDIT_RUN_BLOCK = /(?:^|\n)Run `([^\n]*)`:\n([\s\S]*)$/;
const EXIT_NOTICE = /\bCommand exited with code (\d+)\b/;
const TIMEOUT_NOTICE = /\[Command timed out\b/;

function textOf(content: Msg["content"]): string {
	if (typeof content === "string") return content;
	return (content ?? [])
		.filter(p => p.type === "text")
		.map(p => p.text ?? "")
		.join("\n");
}

function clip(s: string, n: number): string {
	const one = s.replace(/\s+/g, " ").trim();
	return one.length > n ? `${one.slice(0, n - 1)}…` : one;
}

function sentenceAround(text: string, index: number): string {
	const start = Math.max(text.lastIndexOf(".", index), text.lastIndexOf("\n", index)) + 1;
	const ends = [".", "\n"].map(c => text.indexOf(c, index)).filter(i => i >= 0);
	const end = ends.length ? Math.min(...ends) : text.length;
	return clip(text.slice(start, end), 120);
}

/** True when the message is the user's own (not an engine notice attributed to the agent). */
export function isUserMessage(m: Msg): boolean {
	return m.role === "user" && m.attribution !== "agent";
}

/**
 * Decides whether the agent stopped a turn prematurely. `messages` is the session history; only the part after
 * the last user message counts. Returns undefined when the stop looks deliberate.
 */
export function detectPrematureStop(messages: readonly Msg[]): Verdict | undefined {
	let start = -1;
	for (let i = messages.length - 1; i >= 0; i--) {
		if (isUserMessage(messages[i])) {
			start = i;
			break;
		}
	}
	if (start < 0) return undefined;
	const turn = messages.slice(start + 1);
	const last = [...turn].reverse().find(m => m.role === "assistant");
	// Errors, length cuts and aborts settle through their own recovery paths.
	if (!last || last.stopReason !== "stop") return undefined;

	// Code blocks quote program output, not the agent's own claims; inline code and emphasis keep their text
	// ("has **not** been rerun").
	const final = textOf(last.content)
		.replace(/```[\s\S]*?```/g, " ")
		.replace(/`([^`\n]*)`/g, "$1")
		.replace(/\*\*|__|(?<!\w)[*_]|[*_](?!\w)/g, "");
	if (QUESTION.test(final) || USER_BLOCKER.test(final)) return undefined;

	for (const re of UNFINISHED) {
		const m = re.exec(final);
		if (m) {
			return {
				kind: "unfinished-admission",
				reason: `your last message says the work is unfinished: "${sentenceAround(final, m.index)}"`,
			};
		}
	}

	const calls = new Map<string, Part>();
	let edited = false;
	let lastShell: { command: string; exitCode: number | null; editedAfter: boolean } | undefined;
	for (const m of turn) {
		if (m.role === "assistant" && Array.isArray(m.content)) {
			for (const p of m.content) if (p.type === "toolCall" && p.id) calls.set(p.id, p);
			continue;
		}
		if (m.role !== "toolResult") continue;
		const name = m.toolName ?? "";
		if (EDIT_TOOLS[name]) {
			if (!m.isError) {
				edited = true;
				if (lastShell) lastShell.editedAfter = true;
				const ran = EDIT_RUN_BLOCK.exec(textOf(m.content));
				if (ran) {
					const exit = EXIT_NOTICE.exec(ran[2]);
					const exitCode = exit ? Number(exit[1]) : TIMEOUT_NOTICE.test(ran[2]) ? null : 0;
					lastShell = { command: ran[1], exitCode, editedAfter: false };
				}
			}
			continue;
		}
		if (name !== "bash") continue;
		const command = String(calls.get(m.toolCallId ?? "")?.arguments?.command ?? "");
		const exitCode = typeof m.details?.exitCode === "number" ? m.details.exitCode : m.isError ? null : 0;
		if (exitCode === 0 && SHELL_EDIT.test(command)) edited = true;
		lastShell = { command, exitCode, editedAfter: false };
	}

	if (lastShell && lastShell.exitCode !== 0 && !lastShell.editedAfter) {
		const benign = lastShell.exitCode === 1 && BENIGN_EXIT_1.test(lastShell.command);
		if (!benign) {
			const code = lastShell.exitCode === null ? "no exit code" : `exit code ${lastShell.exitCode}`;
			return {
				kind: "failed-check",
				reason: `the last command you ran failed (${code}: \`${clip(lastShell.command, 100)}\`) and nothing was changed after it. Find out why it failed: if your change or the task causes it, fix it; if it fails the same way without your change, or cannot run in this environment, say so and show the evidence`,
			};
		}
	}

	const request = textOf(messages[start].content);
	if (!edited && CHANGE_REQUEST.test(request) && !QUESTION_REQUEST.test(request)) {
		return { kind: "no-edit", reason: "the request asks for a change, but no file was edited in this turn" };
	}
	return undefined;
}
