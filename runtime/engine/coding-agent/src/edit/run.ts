/**
 * `edit.run`: an optional shell command attached to an edit, run through the session's `bash`
 * tool after the edit applies cleanly, so an edit and its check share one request.
 *
 * JSON edit calls carry it as a `run` field. Freeform patch grammars (hashline, apply_patch) carry
 * it as a final `*** Run: <command>` line after `*** End Patch`; {@link splitEditRun} lifts either
 * form off the params before the patch is parsed, so the edit engine never sees it.
 */
import { isRecord } from "@oh-my-pi/pi-utils";
import type { ToolSession } from "../tools";

const RUN_LINE_PREFIX = "*** Run:";
/** The `end_patch` rule of the hashline and apply_patch grammars (column padding varies). */
const END_PATCH_RULE = /^end_patch:[ \t]*"\*\*\* End Patch" LF\?[ \t]*$/m;

/** A trailing `*** Run: <command>` line directly after the `*** End Patch` closer. */
const TRAILING_RUN_LINE = /(\*\*\* End Patch)[ \t]*\r?\n\*\*\* Run:[ \t]*([^\r\n]*?)[ \t]*(?:\r?\n\s*)?$/;

/**
 * The freeform grammar extended with the optional run line, or `undefined` when the grammar has
 * no `*** End Patch` closer to hang it on (sloppy mode: `run` stays a JSON-only field there).
 */
export function grammarWithRunLine(definition: string): string | undefined {
	if (!END_PATCH_RULE.test(definition)) return undefined;
	return definition.replace(END_PATCH_RULE, 'end_patch: "*** End Patch" (LF "*** Run: " /[^\\r\\n]+/)? LF?');
}

/** Tool-description addendum for the `run` option. */
export function editRunDescription(runLine: boolean): string {
	const where = runLine
		? `End the input with a line \`${RUN_LINE_PREFIX} <command>\` after \`*** End Patch\` (or set \`run\`)`
		: "Set `run` to a shell command";
	return `\n\n<run>\n${where} to run that command like \`bash\` once this edit applies cleanly; its output is appended to the result. Use it for the check the edit needs (e.g. the test command). It does not run when the edit is rejected or leaves new syntax errors.\n</run>`;
}

/**
 * Splits the run command off edit params. Returns the params the edit engine applies (without
 * `run` and without a trailing run line) and the trimmed command, if any. Params without a run
 * come back as the same object.
 */
export function splitEditRun<T>(params: T): { params: T; run?: string } {
	if (!isRecord(params)) return { params };
	let rest: Record<string, unknown> = params;
	let run: string | undefined;
	if ("run" in params) {
		const { run: field, ...others } = params;
		rest = others;
		if (typeof field === "string" && field.trim()) run = field.trim();
	}
	if (typeof rest.input === "string") {
		const match = TRAILING_RUN_LINE.exec(rest.input);
		if (match) {
			rest = { ...rest, input: `${rest.input.slice(0, match.index)}${match[1]}\n` };
			if (match[2]) run ??= match[2];
		}
	}
	return rest === params ? { params } : { params: rest as T, ...(run ? { run } : {}) };
}

/**
 * Runs `command` through the session's enabled `bash` tool, the same entry the eval bridge uses
 * (the session's approval gate, ACP permission and output truncation apply), and returns the
 * block appended to the edit result.
 */
export async function runEditCommand(
	session: ToolSession,
	toolCallId: string,
	command: string,
	signal?: AbortSignal,
): Promise<string> {
	const bash = session.getToolForEvalBridge?.("bash");
	if (!bash) return `Run \`${command}\` skipped: the \`bash\` tool is not available in this session.`;
	const header = `Run \`${command}\`:`;
	try {
		const result = await bash.execute(
			`${toolCallId}-run`,
			{ command },
			signal,
			undefined,
			session.getToolContext?.(),
		);
		const text = result.content
			.flatMap(block => (block.type === "text" && block.text ? [block.text] : []))
			.join("\n");
		return `${header}\n${text || "(no output)"}`;
	} catch (error) {
		return `${header}\n${error instanceof Error ? error.message : String(error)}`;
	}
}
