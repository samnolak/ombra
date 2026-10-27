import * as path from "node:path";
import { $flag } from "@oh-my-pi/pi-utils";
import type { ToolSession } from ".";
import { sanitizeDisplayWarnings } from "@oh-my-pi/pi-tui/render/render-utils";
import { isUntrustedProjectScope } from "../config/settings-context";
import { checkPythonKernelAvailability } from "../eval/py/kernel";
import { cfgEvalJs, cfgEvalProbePython, cfgEvalPy, cfgPythonInterpreter } from "../eval/settings";

export interface EvalBackendsAllowance {
	python: boolean;
	js: boolean;
	/** Why the session-start probe (`eval.probePython`) dropped Python; undefined when it did not. */
	pythonUnavailable?: string;
}

/**
 * Settled session-start probe outcomes (`eval.probePython`): `null` = Python
 * starts, a string = why it does not. Failures are cached as well as successes,
 * so the advertised schema stays stable for the process.
 */
const pythonProbeOutcomes = new Map<string, string | null>();

function pythonProbeKey(session: Pick<ToolSession, "cwd" | "settings">): string | undefined {
	if (!session.cwd) return undefined;
	const interpreter = cfgPythonInterpreter.get(session.settings)?.trim() ?? "";
	return `${path.resolve(session.cwd)}\0${interpreter}\0${isUntrustedProjectScope() ? "untrusted" : "trusted"}`;
}

/**
 * With `eval.probePython` on, check once (per cwd + interpreter, cached) whether
 * the Python backend can start, so {@link resolveEvalBackends} advertises only
 * runtimes that work. `PI_PYTHON_SKIP_CHECK` keeps Python advertised unprobed.
 */
export async function probeEvalPython(session: ToolSession, signal?: AbortSignal): Promise<void> {
	if (!cfgEvalProbePython.get(session.settings) || !cfgEvalPy.get(session.settings)) return;
	if ($flag("PI_PYTHON_SKIP_CHECK")) return;
	const key = pythonProbeKey(session);
	if (key === undefined || pythonProbeOutcomes.has(key)) return;
	const interpreter = cfgPythonInterpreter.get(session.settings)?.trim() || undefined;
	const availability = await checkPythonKernelAvailability(session.cwd, interpreter, { forceProbe: true, signal });
	// A cancelled probe proves nothing; the next session start probes again.
	if (signal?.aborted) return;
	if (availability.ok) {
		pythonProbeOutcomes.set(key, null);
		return;
	}
	// The reason embeds interpreter paths and spawn errors: shorten home, strip controls, bound length.
	const [reason] = sanitizeDisplayWarnings([availability.reason ?? "no working Python interpreter"]);
	pythonProbeOutcomes.set(key, reason);
}

/** Test hook: forget cached session-start probe outcomes. */
export function resetEvalPythonProbes(): void {
	pythonProbeOutcomes.clear();
}

/**
 * Active eval backend allowance (`eval.py` / `eval.js`; `PI_PY` / `PI_JS` override).
 * A failed {@link probeEvalPython} outcome (only recorded with `eval.probePython`) turns Python off.
 */
export function resolveEvalBackends(session: ToolSession): EvalBackendsAllowance {
	const python = cfgEvalPy.get(session.settings);
	const js = cfgEvalJs.get(session.settings);
	if (python && cfgEvalProbePython.get(session.settings)) {
		const key = pythonProbeKey(session);
		const reason = key === undefined ? undefined : pythonProbeOutcomes.get(key);
		if (typeof reason === "string") return { python: false, js, pythonUnavailable: reason };
	}
	return { python, js };
}
