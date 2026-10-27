/**
 * Credential scrub for processes the model starts (`bash.envScrub.*`), ported
 * from DeepSeek Harness's `scrubbedParentEnv`: ambient variables whose name
 * looks like a credential are dropped before the command runs, and env the
 * caller passes for one call is applied after the scrub.
 *
 * The embedded shell and the native PTY start from the process environ and
 * only overlay the env they are handed, so leaving a name out of that map is
 * not enough. Shell-line backends also get an `unset -v` prefix
 * ({@link scrubShellLine}); `Bun.spawn` children receive a filtered map
 * ({@link scrubEnv}). The match is a name heuristic, not a secret detector.
 */
import { isPosixShell } from "@oh-my-pi/pi-utils/procmgr";
import type { Settings } from "../config/settings";
import { cfgBashEnvScrubEnabled, cfgBashEnvScrubForward } from "./settings";

/** Credential-shaped variable names, matched case-insensitively. */
export const SENSITIVE_ENV_PATTERN = /KEY|PASSWORD|SECRET|TOKEN/i;

/**
 * Common names that match {@link SENSITIVE_ENV_PATTERN} but configure a tool
 * rather than carry a credential. `SSH_AUTH_SOCK`, `GPG_TTY` and `GIT_ASKPASS`
 * do not match, so they need no entry.
 */
export const ENV_SCRUB_BUILTIN_KEEP: readonly string[] = [
	// Hugging Face tokenizers thread switch.
	"TOKENIZERS_PARALLELISM",
	// Python `keyring` backend selector.
	"PYTHON_KEYRING_BACKEND",
	// GNOME keyring daemon control socket directory.
	"GNOME_KEYRING_CONTROL",
	// pass(1) store location.
	"PASSWORD_STORE_DIR",
];

export interface EnvScrubPolicy {
	/** Upper-cased names kept even though they match the pattern. */
	readonly keep: ReadonlySet<string>;
}

const SHELL_VARIABLE_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** Policy keeping the built-in non-secret names plus `forward`. */
export function createEnvScrubPolicy(forward: readonly string[] = []): EnvScrubPolicy {
	const keep = new Set<string>();
	for (const name of [...ENV_SCRUB_BUILTIN_KEEP, ...forward]) {
		const normalized = name.trim().toUpperCase();
		if (normalized) keep.add(normalized);
	}
	return { keep };
}

/** The session's scrub policy, or `undefined` when `bash.envScrub.enabled` is off. */
export function resolveEnvScrubPolicy(settings: Settings): EnvScrubPolicy | undefined {
	if (!cfgBashEnvScrubEnabled.get(settings)) return undefined;
	return createEnvScrubPolicy(cfgBashEnvScrubForward.get(settings));
}

export function isScrubbedEnvName(name: string, policy: EnvScrubPolicy): boolean {
	return SENSITIVE_ENV_PATTERN.test(name) && !policy.keep.has(name.toUpperCase());
}

/** Copy of `env` without scrubbed names. Merge per-call env after this. */
export function scrubEnv<T extends string | undefined>(
	env: Readonly<Record<string, T>>,
	policy: EnvScrubPolicy,
): Record<string, T> {
	const result: Record<string, T> = {};
	for (const name in env) {
		if (!isScrubbedEnvName(name, policy)) result[name] = env[name];
	}
	return result;
}

/**
 * Scrubbed names a child would inherit from the process environment or from
 * `base`, minus the names `explicit` sets for this call. Sorted.
 */
export function scrubbedEnvNames(
	policy: EnvScrubPolicy,
	options: {
		base?: Readonly<Record<string, string | undefined>>;
		explicit?: Readonly<Record<string, string>>;
	} = {},
): string[] {
	const names = new Set<string>();
	const collect = (source: Readonly<Record<string, string | undefined>>): void => {
		for (const name in source) {
			if (source[name] === undefined || !isScrubbedEnvName(name, policy)) continue;
			if (options.explicit && Object.hasOwn(options.explicit, name)) continue;
			names.add(name);
		}
	};
	collect(Bun.env);
	if (options.base) collect(options.base);
	return [...names].sort();
}

/**
 * Remove `names` for a command line run by `shell` (`undefined` = the embedded
 * POSIX shell). POSIX shells get an `unset -v` prefix; names that are not
 * shell identifiers cannot be unset there. Other shells (cmd.exe, PowerShell)
 * get `blankEnv`, an overlay that empties each name.
 */
export function scrubShellLine(
	shell: string | undefined,
	command: string,
	names: readonly string[],
): { command: string; blankEnv: Record<string, string> } {
	if (names.length === 0) return { command, blankEnv: {} };
	if (shell === undefined || isPosixShell(shell)) {
		const unset = names.filter(name => SHELL_VARIABLE_NAME.test(name));
		return { command: unset.length > 0 ? `unset -v ${unset.join(" ")}; ${command}` : command, blankEnv: {} };
	}
	const blankEnv: Record<string, string> = {};
	for (const name of names) blankEnv[name] = "";
	return { command, blankEnv };
}
