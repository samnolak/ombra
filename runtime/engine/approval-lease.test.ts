import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, rm } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { MCPTool } from "./coding-agent/src/mcp/tool-bridge";
import {
	checkSessionLeaseTakeoversNow,
	FileSessionStorage,
	onSessionLeaseTakeover,
	requestSessionLeaseTakeover,
	withdrawSessionLeaseTakeover,
} from "./coding-agent/src/session/session-storage";
import { buildToolApprovalRequest, formatApprovalPrompt } from "./coding-agent/src/tools/approval";

// ReviewLease findings, each first written to fail against the unfixed engine.
describe("typed approval requests", () => {
	test("a prompt the tool insists on (override) offers only once, like provider safety checks", () => {
		const bash = { name: "bash" };
		const critical = buildToolApprovalRequest(bash, { command: "rm -rf /x" }, { risk: "exec", reason: "Critical pattern detected", override: true });
		expect(critical.scopes).toEqual(["once"]);
		const ordinary = buildToolApprovalRequest(bash, { command: "npm test" }, { risk: "exec" });
		expect(ordinary.scopes).toEqual(["once", "session", "always"]);
	});

	test("bash's cwd is part of the request", () => {
		const bash = { name: "bash", formatApprovalDetails: (args: unknown) => [`Command: ${String((args as { command: string }).command)}`] };
		const request = buildToolApprovalRequest(bash, { command: "npm test", cwd: "../other" }, { risk: "exec" });
		expect(request.cwd).toBe("../other");
		expect(buildToolApprovalRequest(bash, { command: "npm test", cwd: "  " }, { risk: "exec" }).cwd).toBeUndefined();
	});

	test("MCP tools are marked with their server and show their arguments", () => {
		const connection = { name: "docs" } as ConstructorParameters<typeof MCPTool>[0];
		const tool = new MCPTool(connection, { name: "lookup", inputSchema: { type: "object", properties: {} } });
		const request = buildToolApprovalRequest(tool, { query: "leases" }, { risk: "write" });
		expect(request).toMatchObject({ origin: "mcp", server: "docs", details: ['Arguments:\n{\n  "query": "leases"\n}'] });
		expect(formatApprovalPrompt(tool, { query: "leases" })).toContain("Origin: MCP server tool (docs)");
		const long = buildToolApprovalRequest(tool, { query: "x".repeat(5000) }, { risk: "write" });
		expect(long.details[0]!.length).toBeLessThan(2100);
	});
});

describe("session lease takeover requests", () => {
	const root = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "approval-lease");
	beforeAll(() => mkdir(root, { recursive: true }));
	afterAll(() => rm(root, { recursive: true, force: true }));

	test("a request left for an earlier holder is dropped, not obeyed by the next one", () => {
		const id = crypto.randomUUID();
		const file = path.join(root, `${id}.jsonl`);
		const storage = new FileSessionStorage();
		const first = storage.claimSession(id, file)!;
		requestSessionLeaseTakeover(id); // the requester then went away without withdrawing it
		first();
		const second = storage.claimSession(id, file)!;
		let asked = 0;
		const off = onSessionLeaseTakeover(id, () => asked++);
		checkSessionLeaseTakeoversNow();
		expect(asked).toBe(0);
		// A request for this claim is obeyed.
		requestSessionLeaseTakeover(id);
		checkSessionLeaseTakeoversNow();
		expect(asked).toBe(1);
		off();
		second();
	});

	test("one requester withdrawing never removes another requester's request", () => {
		const id = crypto.randomUUID();
		const storage = new FileSessionStorage();
		const release = storage.claimSession(id, path.join(root, `${id}.jsonl`))!;
		let asked = 0;
		const off = onSessionLeaseTakeover(id, () => asked++);
		const a = requestSessionLeaseTakeover(id);
		requestSessionLeaseTakeover(id);
		withdrawSessionLeaseTakeover(id, a);
		checkSessionLeaseTakeoversNow();
		expect(asked).toBe(1);
		off();
		release();
	});
});
