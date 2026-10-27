import { describe, expect, test } from "bun:test";
import * as path from "node:path";
import { Settings } from "./coding-agent/src/config/settings";
import { type JsEvalWorkerHandle, setJsEvalWorkerFactoriesForTests } from "./coding-agent/src/eval/js/context-manager";
import { executeJs } from "./coding-agent/src/eval/js/executor";

const SRC = path.join(import.meta.dir, "coding-agent", "src");
const MARKER = "ENV_SCRUB_RESULT ";

/**
 * The embedded shell inherits the process environ, which `process.env` writes
 * never reach, so the ambient variables must be in a real child's environment.
 * The child runs `env` through the bash tool and through `executeBash` with a
 * per-call env, and prints the matching lines.
 */
async function runBashChild(enabled: boolean | undefined): Promise<{ tool: string; explicit: string }> {
	const script = `
		import { Settings } from ${JSON.stringify(path.join(SRC, "config/settings.ts"))};
		import { withActiveSettings } from ${JSON.stringify(path.join(SRC, "config/settings-context.ts"))};
		import { executeBash } from ${JSON.stringify(path.join(SRC, "exec/bash-executor.ts"))};
		import { resolveEnvScrubPolicy } from ${JSON.stringify(path.join(SRC, "exec/env-scrub.ts"))};
		import { BashTool } from ${JSON.stringify(path.join(SRC, "tools/bash.ts"))};
		const settings = Settings.isolated({
			${enabled === undefined ? "" : `"bash.envScrub.enabled": ${enabled},`}
			"bash.envScrub.forward": ["GH_TOKEN"],
			"bash.autoBackground.enabled": false,
			"bash.direnv": "off",
			"shellMinimizer.enabled": false,
		});
		const command = "env | grep -E '^(ENVSCRUB_|GH_TOKEN=)' | sort";
		const tool = new BashTool({
			settings,
			cwd: process.cwd(),
			getSessionId: () => "env-scrub",
			getSessionFile: () => null,
		});
		const result = await withActiveSettings(settings, () => tool.execute("call-1", { command }));
		const explicit = await withActiveSettings(settings, () =>
			executeBash(command, {
				sessionKey: "env-scrub-explicit",
				envScrub: resolveEnvScrubPolicy(settings),
				env: { ENVSCRUB_CALL_TOKEN: "explicit" },
			}),
		);
		const toolText = result.content.map(block => (block.type === "text" ? block.text : "")).join("");
		console.log(${JSON.stringify(MARKER)} + JSON.stringify({ tool: toolText, explicit: explicit.output }));
		process.exit(0);
	`;
	const proc = Bun.spawn([process.execPath, "-e", script], {
		cwd: import.meta.dir,
		env: {
			...process.env,
			ENVSCRUB_AMBIENT_TOKEN: "ambient-value",
			ENVSCRUB_CALL_TOKEN: "ambient-value",
			ENVSCRUB_PLAIN: "plain-value",
			GH_TOKEN: "forwarded-value",
		},
		stdout: "pipe",
		stderr: "pipe",
	});
	const [stdout, stderr, exitCode] = await Promise.all([
		new Response(proc.stdout).text(),
		new Response(proc.stderr).text(),
		proc.exited,
	]);
	const line = stdout.split("\n").find(entry => entry.startsWith(MARKER));
	if (exitCode !== 0 || !line) throw new Error(`child failed (${exitCode}):\n${stdout}\n${stderr}`);
	return JSON.parse(line.slice(MARKER.length));
}

describe("bash.envScrub", () => {
	test("drops credential-named ambient env from bash children, keeps forwarded and per-call names", async () => {
		const { tool, explicit } = await runBashChild(true);
		expect(tool).not.toContain("ENVSCRUB_AMBIENT_TOKEN");
		expect(tool).not.toContain("ENVSCRUB_CALL_TOKEN");
		expect(tool).toContain("GH_TOKEN=forwarded-value");
		expect(tool).toContain("ENVSCRUB_PLAIN=plain-value");
		expect(explicit).not.toContain("ENVSCRUB_AMBIENT_TOKEN");
		expect(explicit).toContain("ENVSCRUB_CALL_TOKEN=explicit");
		expect(explicit).toContain("GH_TOKEN=forwarded-value");
	}, 60_000);

	test("off by default in the registry: the ambient env is inherited unchanged", async () => {
		const { tool } = await runBashChild(undefined);
		expect(tool).toContain("ENVSCRUB_AMBIENT_TOKEN=ambient-value");
		expect(tool).toContain("ENVSCRUB_CALL_TOKEN=ambient-value");
	}, 60_000);
});

/** A JS eval runtime whose init handshake fails with `error`. */
function failingJsRuntime(mode: JsEvalWorkerHandle["mode"], error: string): JsEvalWorkerHandle {
	const errorHandlers = new Set<(error: Error) => void>();
	return {
		mode,
		send: () => queueMicrotask(() => errorHandlers.forEach(handler => handler(new Error(error)))),
		onMessage: () => () => {},
		onError: handler => {
			errorHandlers.add(handler);
			return () => errorHandlers.delete(handler);
		},
		close: async () => true,
		terminate: async () => {},
	};
}

describe("bash.envScrub JS eval", () => {
	// The in-process Worker fallback shares the engine's environment.
	async function startJs(enabled: boolean, spawnProcess: () => JsEvalWorkerHandle) {
		let workerFallbacks = 0;
		const restore = setJsEvalWorkerFactoriesForTests({
			spawnProcess,
			spawnWorker: () => {
				workerFallbacks++;
				return failingJsRuntime("worker", "worker init failed");
			},
		});
		const settings = Settings.isolated({ "bash.envScrub.enabled": enabled });
		const session = { settings, cwd: import.meta.dir, getSessionId: () => "env-scrub-js", getSessionFile: () => null };
		let failure = "";
		try {
			const result = await executeJs("1", {
				sessionId: `env-scrub-js-${enabled}-${Math.random()}`,
				session: session as unknown as Parameters<typeof executeJs>[1]["session"],
				cwd: import.meta.dir,
			} as Parameters<typeof executeJs>[1]);
			failure = result.output;
		} catch (error) {
			failure = String(error);
		} finally {
			restore();
		}
		return { workerFallbacks, failure };
	}

	for (const [stage, spawnProcess] of [
		[
			"spawn",
			(): JsEvalWorkerHandle => {
				throw new Error("subprocess spawn failed");
			},
		],
		["init", () => failingJsRuntime("process", "subprocess init failed")],
	] as const) {
		test(`a subprocess ${stage} failure never falls back to the shared-env Worker while scrubbing`, async () => {
			const on = await startJs(true, spawnProcess);
			expect(on.workerFallbacks).toBe(0);
			expect(on.failure).toContain(`subprocess ${stage} failed`);
			const off = await startJs(false, spawnProcess);
			expect(off.workerFallbacks).toBe(1);
		});
	}
});
