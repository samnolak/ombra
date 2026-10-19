import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { realpathSync } from "node:fs";
import { mkdir, mkdtemp, readdir, readFile, rm } from "node:fs/promises";
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
import { createRpcSessionRuntime } from "./rpc-mode";

type Frame = Record<string, unknown>;

const textOf = (message: Context["messages"][number]): string => {
	if (!("content" in message)) return "";
	const content: unknown = message.content;
	if (typeof content === "string") return content;
	if (!Array.isArray(content)) return "";
	return content.map(part => (isRecord(part) && typeof part.text === "string" ? part.text : "")).join("");
};

/** The stand-in model: a reply to a user message with "SLOW" streams until aborted, anything else answers at once. */
function standInModel() {
	const waiters: Array<{ count: number; resolve: () => void }> = [];
	const model = createMockModel({
		id: "navigate-tree-mock",
		handler: async (context): Promise<MockResponse> => {
			for (const waiter of waiters.filter(w => model.calls.length >= w.count)) {
				waiters.splice(waiters.indexOf(waiter), 1);
				waiter.resolve();
			}
			const lastUser = context.messages.findLast(message => message.role === "user");
			const text = lastUser ? textOf(lastUser) : "";
			if (text.includes("SLOW")) return { content: ["Streaming a long reply."], delayMs: 60_000 };
			return { content: [`Reply to: ${text.slice(0, 40)}`] };
		},
	});
	return {
		model,
		requested: (count: number): Promise<void> => {
			if (model.calls.length >= count) return Promise.resolve();
			const reached = Promise.withResolvers<void>();
			waiters.push({ count, resolve: reached.resolve });
			return reached.promise;
		},
	};
}

describe("RPC navigate_tree", () => {
	const root = path.join(realpathSync(os.tmpdir()), "ombra-tests", `run-${process.pid}`, "rpc-navigate-tree");
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
		const sessionDir = path.join(cwd, ".sessions");
		const sessionManager = SessionManager.create(cwd, sessionDir);
		const settings = await Settings.loadIsolated({ cwd, agentDir: getAgentDir(), projectRoot: cwd, inMemory: true });
		const standIn = standInModel();
		const registry = new AgentRegistry();
		const { session } = await AgentRegistry.runWithRegistry(registry, () =>
			withActiveSettings(settings, () =>
				createAgentSession({
					cwd, settings, sessionManager, agentRegistry: registry, model: standIn.model, bindProcessState: false,
					disableExtensionDiscovery: true, skills: [], rules: [], enableLsp: false,
				}),
			),
		);
		session.modelRegistry.authStorage.keys.setRuntime(standIn.model.provider, "mock-key");
		const frames: Frame[] = [];
		const waiters: Array<{ match: (frame: Frame) => boolean; resolve: (frame: Frame) => void }> = [];
		const runtime = await withActiveSettings(settings, () =>
			createRpcSessionRuntime(session, {
				protocolVersion: 2,
				output: frame => {
					if (!isRecord(frame)) return;
					frames.push(frame);
					for (const waiter of waiters.filter(w => w.match(frame))) {
						waiters.splice(waiters.indexOf(waiter), 1);
						waiter.resolve(frame);
					}
				},
			}),
		);
		const next = (match: (frame: Frame) => boolean): Promise<Frame> => {
			const seen = frames.find(match);
			if (seen) return Promise.resolve(seen);
			const waiting = Promise.withResolvers<Frame>();
			waiters.push({ match, resolve: waiting.resolve });
			return waiting.promise;
		};
		let ids = 0;
		const send = (command: Frame): string => {
			const id = `r${++ids}`;
			runtime.dispatch({ ...command, id });
			return id;
		};
		const response = (id: string) => next(frame => frame.type === "response" && frame.id === id);
		const promptResult = (id: string) => next(frame => frame.type === "prompt_result" && frame.id === id);
		const sessionFiles = async () => (await readdir(sessionDir)).filter(name => name.endsWith(".jsonl"));
		const userTexts = () =>
			session.sessionManager
				.buildSessionContext()
				.messages.filter(message => message.role === "user")
				.map(textOf);
		const branchEntry = async (text: string): Promise<string> => {
			const listed = await response(send({ type: "get_branch_messages" }));
			const messages = isRecord(listed.data) && Array.isArray(listed.data.messages) ? listed.data.messages : [];
			const entry: unknown = messages.findLast(message => isRecord(message) && message.text === text);
			if (!isRecord(entry) || typeof entry.entryId !== "string") throw new Error(`no branch message "${text}"`);
			return entry.entryId;
		};
		return {
			session, runtime, standIn, send, response, promptResult, sessionFiles, userTexts, branchEntry,
			request: (command: Frame) => response(send(command)),
		};
	}

	test("rewinding a stopped last message stays in the same session file, also after the chat is reopened", async () => {
		const h = await open();
		try {
			expect(await h.promptResult(h.send({ type: "prompt", message: "first question" }))).toMatchObject({
				status: "completed",
			});
			// The owner's case: a message whose turn is stopped (Stop, or the app quitting), then Rewind to here on it.
			const slow = h.send({ type: "prompt", message: "SLOW second question" });
			await h.standIn.requested(2);
			expect(await h.request({ type: "abort" })).toMatchObject({ success: true });
			expect(await h.promptResult(slow)).toMatchObject({ status: "aborted" });
			await h.session.sessionManager.flush();
			const file = h.session.sessionFile;
			expect(file).toBeString();
			expect(await h.sessionFiles()).toEqual([path.basename(file!)]);

			const rewound = await h.request({
				type: "navigate_tree",
				targetId: await h.branchEntry("SLOW second question"),
			});
			expect(rewound).toMatchObject({
				success: true,
				command: "navigate_tree",
				data: { text: "SLOW second question", cancelled: false },
			});
			// In place: no copy of the conversation, no second chat in the list, the same session id.
			expect(h.session.sessionFile).toBe(file);
			expect(await h.sessionFiles()).toEqual([path.basename(file!)]);
			expect(h.userTexts()).toEqual(["first question"]);

			expect(await h.promptResult(h.send({ type: "prompt", message: "second question again" }))).toMatchObject({
				status: "completed",
			});
			await h.session.sessionManager.flush();
			expect(h.session.sessionFile).toBe(file);
			expect(await h.sessionFiles()).toEqual([path.basename(file!)]);
			expect(h.userTexts()).toEqual(["first question", "second question again"]);
			// The replaced path is kept in the same file, as a sibling branch (omp's /tree).
			expect(await readFile(file!, "utf8")).toContain("SLOW second question");

			// Resume after a restart continues the rewound branch of the same file.
			const reopened = await SessionManager.open(file!, path.dirname(file!));
			try {
				expect(reopened.getSessionFile()).toBe(file);
				expect(
					reopened
						.buildSessionContext()
						.messages.filter(message => message.role === "user")
						.map(textOf),
				).toEqual(["first question", "second question again"]);
			} finally {
				await reopened.close();
			}
		} finally {
			await h.runtime.close();
		}
	}, 60_000);

	test("is refused while a reply runs, leaving the conversation as it is", async () => {
		const h = await open();
		try {
			expect(await h.promptResult(h.send({ type: "prompt", message: "first question" }))).toMatchObject({
				status: "completed",
			});
			const target = await h.branchEntry("first question");
			const slow = h.send({ type: "prompt", message: "SLOW running" });
			await h.standIn.requested(2);
			expect(await h.request({ type: "navigate_tree", targetId: target })).toMatchObject({
				success: false,
				command: "navigate_tree",
				code: "session_busy",
			});
			expect(await h.request({ type: "abort" })).toMatchObject({ success: true });
			expect(await h.promptResult(slow)).toMatchObject({ status: "aborted" });
			expect(h.userTexts()).toEqual(["first question", "SLOW running"]);
		} finally {
			await h.runtime.close();
		}
	}, 60_000);
});
