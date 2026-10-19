import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { AsyncJobManager } from "./coding-agent/src/async/job-manager";
import { JobOutputLog } from "./coding-agent/src/async/job-output";
import { createRpcSessionRuntime, type RpcSessionRuntime } from "./coding-agent/src/modes/rpc/rpc-mode";
import { listRpcJobs, looksLikeInputPrompt, RPC_JOB_INPUT_IDLE_MS, readRpcJobOutput, type RpcJobsSession } from "./coding-agent/src/modes/rpc/rpc-jobs";
import type { RpcJobOutputResult, RpcJobSnapshot } from "./coding-agent/src/modes/rpc/rpc-types";
import { createEngineSession } from "./session-factory";

describe("job output log", () => {
	test("offsets survive retention, reads are bounded and never split a character", () => {
		const log = new JobOutputLog(16);
		log.append("abc", 1);
		log.append("é漢😀", 2); // 2 + 3 + 4 bytes
		expect(log.totalBytes).toBe(12);
		expect(log.startByte).toBe(0);
		expect(log.lastOutputAt).toBe(2);
		// Pages of 4 bytes: a cut character moves to the next page, nothing is lost or duplicated
		let text = "";
		for (let from = 0; from < log.totalBytes; ) {
			const slice = log.read(from, 4);
			expect(slice.fromByte).toBe(from);
			expect(slice.nextByte).toBeGreaterThan(from);
			text += slice.text;
			from = slice.nextByte;
		}
		expect(text).toBe("abcé漢😀");
		expect(log.tail(5)).toEqual({ fromByte: 8, nextByte: 12, text: "😀" });

		log.append("0123456789", 3); // 22 bytes printed, the newest 16 kept
		expect(log.totalBytes).toBe(22);
		expect(log.startByte).toBe(6);
		// A cursor in the dropped bytes starts at the oldest kept character boundary
		const late = log.read(0, 100);
		expect(late.fromByte).toBe(8);
		expect(late.text).toBe("😀0123456789");
		expect(log.read(22, 10)).toEqual({ fromByte: 22, nextByte: 22, text: "" });
	});

	test("a page holds at least one whole character, so a cursor always moves", () => {
		const log = new JobOutputLog();
		log.append("😀é", 1);
		expect(log.read(0, 1)).toEqual({ fromByte: 0, nextByte: 4, text: "😀" });
		let text = "";
		for (let from = 0, pages = 0; from < log.totalBytes; pages++) {
			const slice = log.read(from, 1);
			expect(slice.nextByte).toBeGreaterThan(from);
			text += slice.text;
			from = slice.nextByte;
			expect(pages).toBeLessThan(10);
		}
		expect(text).toBe("😀é");
		expect(log.tail(1)).toEqual({ fromByte: 4, nextByte: 6, text: "é" });
	});

	test("get_job_output counts bytes lost to retention up to the first whole character it returns", () => {
		const log = new JobOutputLog(16);
		log.append("abcé漢😀0123456789", 1); // 22 bytes; the kept 16 start inside 漢, the first whole character is at 8
		expect(log.startByte).toBe(6);
		const item = { id: "bg_1", type: "bash" as const, status: "running" as const, label: "x", startTime: 0 };
		const session = {
			getAsyncJobSnapshot: () => ({ running: [item], recent: [], delivery: { queued: 0, delivering: false, pendingJobIds: [] } }),
			inspectAsyncJob: () => ({ pids: [], outputLog: log }),
			cancelAsyncJob: () => false,
		};
		const read = readRpcJobOutput(session as unknown as RpcJobsSession, { jobId: "bg_1", fromByte: 3 });
		expect(read).toMatchObject({ fromByte: 8, skippedBytes: 5, text: "😀0123456789", nextByte: 22 });
		expect(readRpcJobOutput(session as unknown as RpcJobsSession, { jobId: "bg_1", fromByte: 8 }).skippedBytes).toBe(0);
	});

	test("all jobs' logs share one byte budget: settled jobs give up their output first, oldest first", async () => {
		const manager = new AsyncJobManager({ outputBudgetBytes: 2500 });
		const finish: Array<() => void> = [];
		const start = (text: string) =>
			manager.register("bash", text.slice(0, 5), async ({ appendOutput }) => {
				appendOutput(text);
				const done = Promise.withResolvers<void>();
				finish.push(done.resolve);
				await done.promise;
				return "ok";
			});
		try {
			const first = start("a".repeat(1000));
			const second = start("b".repeat(1000));
			finish.shift()!();
			finish.shift()!();
			await manager.getJob(first)!.promise;
			await manager.getJob(second)!.promise;
			const running = start("c".repeat(1000)); // 3000 bytes held: over the budget
			const log = (id: string) => manager.getJob(id)!.output!;
			expect(log(first).startByte).toBe(1000); // the oldest settled job's output is released
			expect(log(first).totalBytes).toBe(1000); // its offsets still stand
			expect(log(second).startByte).toBe(0);
			expect(log(running).startByte).toBe(0);
			const fourth = start("d".repeat(1000)); // still over: the next settled one goes
			expect(log(second).startByte).toBe(1000);
			const fifth = start("e".repeat(1000)); // only running jobs left: the largest is trimmed from its start
			const kept = [running, fourth, fifth].reduce((sum, id) => sum + log(id).totalBytes - log(id).startByte, 0);
			expect(kept).toBeLessThanOrEqual(2500);
			expect(log(fifth).read(0, 1000).text.endsWith("e")).toBe(true);
		} finally {
			for (const done of finish) done();
			await manager.dispose({ timeoutMs: 1000 });
		}
	});
});

describe("waiting-for-input heuristic", () => {
	test.each([
		["Continue? [y/N] ", true],
		["Overwrite file.txt? (y/n)\n", true],
		["Enter passphrase for key '/home/u/.ssh/id_ed25519': ", true],
		["\u001b[1mPassword:\u001b[0m ", true],
		["Press Enter to continue\n", true],
		["Name of the project: ", true],
		["mysql> ", true],
		["Do you want to proceed?\n", true],
		["Compiling 12 modules\n", false],
		["ready on http://localhost:3000\n", false],
		["error: build failed: 3 errors\n", false],
		["", false],
		["\n\n", false],
	])("%j → %p", (tail, expected) => {
		expect(looksLikeInputPrompt(tail)).toBe(expected);
	});
});

describe("background jobs over the real engine's RPC", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-background-jobs");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	test("get_jobs lists real bash jobs; get_job_output pages by byte cursor; waiting for input; cancel_job stops one", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "jobs-"));
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("jobs-regression", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-regression-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }]
		});`);
		const args = [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--tools", "bash", "--model", "jobs-regression/model", "--no-session",
		];
		const created = await createEngineSession(cwd, args);
		const waiting = new Map<string, (frame: Record<string, unknown>) => void>();
		let runtime: RpcSessionRuntime | undefined;
		try {
			await created.runInContext(() =>
				createRpcSessionRuntime(created.session, {
					output: frame => {
						const f = frame as Record<string, unknown>;
						if (f.type === "response" && typeof f.id === "string") {
							waiting.get(f.id)?.(f);
							waiting.delete(f.id);
						}
					},
					protocolVersion: 2,
					setToolUIContext: created.setToolUIContext,
					subagentEventBus: created.subagentEventBus,
					headless: true,
					onRuntime: published => {
						runtime = published;
					},
				}),
			);
			if (!runtime) throw new Error("RPC runtime did not publish its dispatcher");
			const live = runtime;
			let seq = 0;
			const request = (frame: Record<string, unknown>): Promise<Record<string, unknown>> => {
				const id = `jobs-${++seq}`;
				const answer = Promise.withResolvers<Record<string, unknown>>();
				waiting.set(id, answer.resolve);
				created.runInContext(() => live.dispatch({ ...frame, id }));
				return answer.promise;
			};
			const data = async <T>(frame: Record<string, unknown>): Promise<T> => {
				const response = await request(frame);
				if (response.success !== true) throw new Error(`${String(frame.type)} failed: ${String(response.error)}`);
				return response.data as T;
			};
			const jobs = async () => (await data<{ jobs: RpcJobSnapshot[] }>({ type: "get_jobs" })).jobs;
			// Real shells and the idle-time heuristic run on the platform clock (no fake timers reach a child
			// process): poll RPC state, never assume a fixed delay.
			const until = async <T>(read: () => Promise<T>, done: (value: T) => boolean, what: string): Promise<T> => {
				const deadline = Date.now() + 15_000;
				for (;;) {
					const value = await read();
					if (done(value)) return value;
					if (Date.now() > deadline) throw new Error(`never reached: ${what}`);
					await Bun.sleep(50);
				}
			};

			expect(await jobs()).toEqual([]);
			const bash = created.session.getToolByName("bash");
			if (!bash) throw new Error("bash tool missing");
			const start = (command: string) =>
				created.runInContext(() => bash.execute(`call-${command.length}`, { command, async: true, pty: false }));
			// 300 lines of multi-byte output, then a prompt the command waits on
			const lines = Array.from({ length: 300 }, (_, i) => `line ${i + 1} é漢😀`);
			const expected = `${lines.join("\n")}\nContinue? [y/N] `;
			await start(`for i in $(seq 1 300); do echo "line $i é漢😀"; done; printf 'Continue? [y/N] '; sleep 60`);
			const expectedBytes = Buffer.byteLength(expected, "utf8");
			const [prompting] = await until(jobs, list => list[0]?.outputBytes === expectedBytes, "all output streamed");
			expect(prompting).toMatchObject({ type: "bash", status: "running", waitingForInput: false });
			expect(prompting!.command).toContain("Continue? [y/N]");
			expect(prompting!.startTime).toBeLessThanOrEqual(Date.now());

			// The tail, bounded
			const tail = await data<RpcJobOutputResult>({ type: "get_job_output", jobId: prompting!.id, maxBytes: 64 });
			expect(tail.text.endsWith("Continue? [y/N] ")).toBe(true);
			expect(tail.nextByte).toBe(expectedBytes);
			expect(tail.nextByte - tail.fromByte).toBeLessThanOrEqual(64);
			expect(tail).toMatchObject({ status: "running", snapshot: false, reset: false, totalBytes: expectedBytes });
			// From byte 0 in small pages: the whole output exactly, no character split
			let text = "";
			let cursor = 0;
			for (let pages = 0; cursor < expectedBytes; pages++) {
				const page = await data<RpcJobOutputResult>({ type: "get_job_output", jobId: prompting!.id, fromByte: cursor, maxBytes: 4096 });
				expect(page.fromByte).toBe(cursor);
				expect(Buffer.byteLength(page.text, "utf8")).toBe(page.nextByte - page.fromByte);
				text += page.text;
				cursor = page.nextByte;
				expect(pages).toBeLessThan(100);
			}
			expect(text).toBe(expected);
			// Nothing new at the cursor; past the end it restarts at the tail
			expect(await data<RpcJobOutputResult>({ type: "get_job_output", jobId: prompting!.id, fromByte: cursor })).toMatchObject({ text: "", fromByte: cursor, nextByte: cursor });
			expect(await data<RpcJobOutputResult>({ type: "get_job_output", jobId: prompting!.id, fromByte: cursor + 10 })).toMatchObject({ reset: true, nextByte: cursor });
			// Bad requests are refused
			expect((await request({ type: "get_job_output", jobId: prompting!.id, maxBytes: 0 })).success).toBe(false);
			expect((await request({ type: "get_job_output", jobId: prompting!.id, fromByte: -1 })).success).toBe(false);
			expect(await request({ type: "get_job_output", jobId: "bg_404" })).toMatchObject({ success: false, error: "Unknown background job: bg_404" });

			// Quiet on a prompt for the idle time: waiting for input, in the list and on the read
			const quiet = await until(jobs, list => list[0]!.waitingForInput, "waiting for input");
			expect(Date.now() - quiet[0]!.lastOutputAt!).toBeGreaterThanOrEqual(RPC_JOB_INPUT_IDLE_MS);
			expect((await data<RpcJobOutputResult>({ type: "get_job_output", jobId: prompting!.id })).waitingForInput).toBe(true);

			// A second job that keeps printing is never waiting; its quiet time is judged from the injected clock too
			await start("for i in $(seq 1 600); do echo tick $i; sleep 0.05; done");
			const ticking = await until(jobs, list => list.length === 2 && list[1]!.outputBytes > 0, "second job prints");
			expect(ticking.map(job => job.status)).toEqual(["running", "running"]);
			expect(ticking[1]!.waitingForInput).toBe(false);
			expect(created.runInContext(() => listRpcJobs(created.session, Date.now() + RPC_JOB_INPUT_IDLE_MS))[1]!.waitingForInput).toBe(false);

			// Stop only the prompting one
			expect(await data({ type: "cancel_job", jobId: prompting!.id })).toEqual({ cancelled: true });
			const afterCancel = await until(jobs, list => list.some(job => job.status === "cancelled" && job.endTime !== undefined), "cancelled job settled");
			expect(afterCancel.map(job => [job.id, job.status])).toEqual([
				[ticking[1]!.id, "running"],
				[prompting!.id, "cancelled"],
			]);
			const cancelled = afterCancel[1]!;
			expect(cancelled.waitingForInput).toBe(false);
			expect(cancelled.endTime).toBeGreaterThanOrEqual(cancelled.startTime);
			expect(await data({ type: "cancel_job", jobId: prompting!.id })).toEqual({ cancelled: false });
			expect(await data({ type: "cancel_job", jobId: "bg_404" })).toEqual({ cancelled: false });
			expect((await request({ type: "cancel_job", jobId: "" })).success).toBe(false);
			// Its output stays readable after it ended
			expect((await data<RpcJobOutputResult>({ type: "get_job_output", jobId: prompting!.id })).status).toBe("cancelled");

			// A finished command reports its exit code
			await start("echo done; exit 3");
			const failed = await until(jobs, list => list.some(job => job.status === "failed"), "failed job listed");
			expect(failed.find(job => job.status === "failed")).toMatchObject({ exitCode: 3, outputBytes: 5 });
			expect(await data({ type: "cancel_job", jobId: ticking[1]!.id })).toEqual({ cancelled: true });
		} finally {
			if (runtime) await runtime.close();
			else await created.runInContext(() => created.session.dispose());
			await rm(cwd, { recursive: true, force: true });
		}
	}, 60_000);
});
