import { afterEach, describe, expect, test, vi } from "bun:test";
import * as os from "node:os";
import { AsyncJobManager } from "./coding-agent/src/async/job-manager";
import { Settings } from "./coding-agent/src/config/settings";
import type { ToolSession } from "./coding-agent/src/tools";
import { WaitTool } from "./coding-agent/src/tools/wait";

// Lab (why-codex-wins §1.2, promises-0): in a print run a hung command was backgrounded and `wait` had no deadline,
// so the run sat until the bench's 300 s kill. `wait.maxHeadlessMs` caps `wait` when the session has no UI and
// says what is still running.
describe("wait.maxHeadlessMs", () => {
	afterEach(() => {
		vi.useRealTimers();
	});

	function setup(hasUI: boolean, capMs: number) {
		const manager = new AsyncJobManager({ maxRunningJobs: 4 });
		// A command that never finishes on its own; the test cancels it.
		const jobId = manager.register("bash", "bun test (hangs)", ({ signal }) =>
			new Promise<string>(resolve => signal.addEventListener("abort", () => resolve("cancelled"), { once: true })),
		);
		const settings = Settings.isolated({ "wait.maxHeadlessMs": capMs, "launch.enabled": false });
		// Only the fields `wait` reads; a full session is not needed to drive the tool.
		const session = { cwd: os.tmpdir(), hasUI, settings, asyncJobManager: manager } as unknown as ToolSession;
		return { tool: new WaitTool(session), jobId, manager };
	}

	async function settledAfter(pending: Promise<unknown>, ms: number): Promise<boolean> {
		let settled = false;
		void pending.then(() => {
			settled = true;
		});
		vi.advanceTimersByTime(ms);
		for (let i = 0; i < 20; i++) await Promise.resolve();
		return settled;
	}

	test("headless: returns 'still running after N s' at the cap", async () => {
		vi.useFakeTimers();
		const { tool, jobId, manager } = setup(false, 2000);
		const pending = tool.execute("call-wait", {});
		expect(await settledAfter(pending, 1999)).toBe(false);
		vi.advanceTimersByTime(1);
		const result = await pending;
		const text = result.content.map(c => ("text" in c ? c.text : "")).join("\n");
		expect(text).toContain(`Still running after 2 s (headless wait limit): \`${jobId}\``);
		expect(text).toContain("write proc://<id>/kill");
		expect(text).toContain("Still Running (1)");
		expect(result.useless).toBe(false);
		manager.cancelAll();
	});

	for (const [name, hasUI, cap] of [
		["with a UI the cap does not apply", true, 2000],
		["0 (default) keeps the built-in ceiling", false, 0],
	] as const) {
		test(name, async () => {
			vi.useFakeTimers();
			const { tool, manager } = setup(hasUI, cap);
			const pending = tool.execute("call-wait", {});
			expect(await settledAfter(pending, 10 * 60_000)).toBe(false);
			manager.cancelAll();
			const result = await pending;
			expect(result.content.map(c => ("text" in c ? c.text : "")).join("\n")).not.toContain("headless wait limit");
		});
	}
});
