import { afterEach, beforeEach, describe, expect, spyOn, test } from "bun:test";
import { createHash } from "node:crypto";
import { realpathSync } from "node:fs";
import * as fs from "node:fs/promises";
import * as path from "node:path";
import { pathToFileURL } from "node:url";
import { applyTextEditsToString, applyWorkspaceEdit } from "../../lsp/edits";
import { applyEditorContent, assertEditorProjectTrusted, canonicalEditorUri, closeEditorDocument, editorDocument, registerEditorDocument, requireEditorProjectTrust, setEditorProjectTrust } from "../../lsp/editor-documents";
import type { EditorDocumentSnapshot } from "../../lsp/editor-documents";
import { addFileDeleteFallback, addFileWriteFallback, deleteFileWithFallback, writeFileWithFallback } from "../../tools/file-write-fallback";
import { createLspWritethrough } from "../../lsp/writethrough";
import { applyAstPreview } from "../../edit/ast-preview";
import { RpcEditorBridge } from "./rpc-editor";
import { notifySaved, sendNotification, syncContent } from "../../lsp/client";
import type { LspClient } from "../../lsp/types";

let directory: string;
let file: string;
let snapshot: EditorDocumentSnapshot;
const owners: object[] = [];
const bridges: RpcEditorBridge[] = [];
beforeEach(async () => {
	const root = path.resolve(".temp/editor-coordination-tests");
	await fs.mkdir(root, { recursive: true });
	directory = await fs.mkdtemp(path.join(root, "run-"));
	file = path.join(directory, "unicode.ts");
	await Bun.write(file, "const greeting = '😀';\n");
	snapshot = { uri: pathToFileURL(file).href, version: 1, text: await Bun.file(file).text(), languageId: "typescript", baseHash: createHash("sha256").update(await Bun.file(file).bytes()).digest("hex"), dirty: false };
});
afterEach(async () => {
	for (const bridge of bridges.splice(0)) { closeEditorDocument(snapshot.uri, bridge); bridge.close(); }
	for (const owner of owners.splice(0)) closeEditorDocument(snapshot.uri, owner);
	setEditorProjectTrust(directory, false);
	requireEditorProjectTrust(false);
	await fs.rm(directory, { recursive: true, force: true });
});

function attach(dirty = false, apply = async (before: EditorDocumentSnapshot, text: string) => ({ ...before, version: before.version + 1, text, dirty: true })) {
	const owner = {};
	owners.push(owner);
	return registerEditorDocument({ ...snapshot, dirty }, owner, apply);
}

function recordingClient(cwd: string): { client: LspClient; writes: (string | Uint8Array)[] } {
	const writes: (string | Uint8Array)[] = [];
	const client: LspClient = {
		name: "editor-security-transport", cwd,
		config: { command: "recording-transport", fileTypes: [".ts"], rootMarkers: [] },
		proc: {
			stdin: { write(data) { writes.push(data); return data.length; }, flush() {} },
			stdout: new ReadableStream({ start(controller) { controller.close(); } }),
			exited: Promise.resolve(0), exitCode: null, kill() {}, peekStderr() { return ""; },
		},
		requestId: 0, diagnostics: new Map(), diagnosticsVersion: 0, openFiles: new Map(), pendingRequests: new Map(),
		isReading: false, status: "ready", lastActivity: 0, writeQueue: Promise.resolve(),
		serverCapabilities: { textDocumentSync: { save: { includeText: true } } },
		activeProgressTokens: new Set(), projectLoaded: Promise.resolve(), resolveProjectLoaded() {},
	};
	return { client, writes };
}

describe("K7 canonical document coordination", () => {
	test("dirty buffer: agent write goes to disk and never touches the unsaved editor text", async () => {
		let called = false;
		attach(true, async () => { called = true; throw new Error("must not be called"); });
		await writeFileWithFallback(file, "agent version\n");
		expect(called).toBe(false);
		expect(await Bun.file(file).text()).toBe("agent version\n");
		expect(editorDocument(file)).toMatchObject({ dirty: true, text: snapshot.text, version: 1 });
	});

	test("conflicted buffer: agent write goes to disk, buffer kept", async () => {
		const owner = {};
		owners.push(owner);
		registerEditorDocument({ ...snapshot, conflict: true }, owner, async () => { throw new Error("must not be called"); });
		await writeFileWithFallback(file, "agent again\n");
		expect(await Bun.file(file).text()).toBe("agent again\n");
		expect(editorDocument(file)?.text).toBe(snapshot.text);
	});

	test("clean write requires a UI acknowledgement and remains unsaved", async () => {
		let called = false;
		attach(false, async (before, text) => {
			called = true;
			expect(before.version).toBe(1);
			expect(before.baseHash).toBe(snapshot.baseHash);
			expect(await Bun.file(file).text()).toBe(snapshot.text);
			return { ...before, version: 2, dirty: true, text };
		});
		await writeFileWithFallback(file, "new buffer\n");
		expect(called).toBe(true);
		expect(editorDocument(file)).toMatchObject({ version: 2, dirty: true, text: "new buffer\n" });
		expect(await Bun.file(file).text()).toBe(snapshot.text);
	});

	test("stale revision and changed disk hash reject before UI or disk mutation", async () => {
		attach();
		await expect(applyEditorContent(file, "bad", { ...snapshot, version: 2 })).rejects.toThrow("Stale");
		await Bun.write(file, "external change");
		await expect(applyEditorContent(file, "bad", snapshot)).rejects.toThrow("changed on disk");
		expect(await Bun.file(file).text()).toBe("external change");
		expect(editorDocument(file)?.text).toBe(snapshot.text);
	});

	test("UI becoming dirty during an in-flight callback never writes disk", async () => {
		attach(false, async () => { throw new Error("UI has unsaved edits"); });
		await expect(writeFileWithFallback(file, "bad")).rejects.toThrow("unsaved");
		expect(await Bun.file(file).text()).toBe(snapshot.text);
	});

	test("workspace edit preflights dirty later files before earlier disk writes", async () => {
		attach(true);
		const other = path.join(directory, "other.ts");
		await Bun.write(other, "old");
		const replacement = [{ range: { start: { line: 0, character: 0 }, end: { line: 0, character: 3 } }, newText: "new" }];
		await expect(applyWorkspaceEdit({ changes: { [pathToFileURL(other).href]: replacement, [snapshot.uri]: replacement } }, directory)).rejects.toThrow("Unsaved");
		expect(await Bun.file(other).text()).toBe("old");
		expect(await Bun.file(file).text()).toBe(snapshot.text);
	});

	test("workspace edits reject stale LSP document versions", async () => {
		attach();
		await expect(applyWorkspaceEdit({ documentChanges: [{ textDocument: { uri: snapshot.uri, version: 9 }, edits: [] }] }, directory)).rejects.toThrow("Stale");
		expect(await Bun.file(file).text()).toBe(snapshot.text);
	});

	test("workspace edit enters the canonical buffer, preserving original disk", async () => {
		attach();
		await applyWorkspaceEdit({ changes: { [snapshot.uri]: [{ range: { start: { line: 0, character: 6 }, end: { line: 0, character: 14 } }, newText: "salutation" }] } }, directory);
		expect(editorDocument(file)?.text).toContain("salutation");
		expect(editorDocument(file)?.dirty).toBe(true);
		expect(await Bun.file(file).text()).toBe(snapshot.text);
	});

	test("UTF-16 edits preserve astral characters before the edit", () => {
		expect(applyTextEditsToString("😀name", [{ range: { start: { line: 0, character: 2 }, end: { line: 0, character: 6 } }, newText: "value" }])).toBe("😀value");
	});

	test("native AST byte ranges preserve Unicode and route clean edits into the UI", async () => {
		attach();
		const start = Buffer.byteLength("const ");
		const updated = applyAstPreview(Buffer.from(snapshot.text), [{ path: file, before: "greeting", after: "salutation", byteStart: start, byteEnd: start + 8, deletedLength: 8, startLine: 1, startColumn: 7, endLine: 1, endColumn: 15 }]);
		await writeFileWithFallback(file, updated.toString("utf8"));
		expect(editorDocument(file)?.text).toBe("const salutation = '😀';\n");
		expect(await Bun.file(file).text()).toBe(snapshot.text);
	});

	test("stale AST preview preimages are rejected without any write", () => {
		expect(() => applyAstPreview(Buffer.from("new"), [{ path: file, before: "old", after: "bad", byteStart: 0, byteEnd: 3, deletedLength: 3, startLine: 1, startColumn: 1, endLine: 1, endColumn: 4 }])).toThrow("Stale");
	});

	test("canonical identity follows symlinks", async () => {
		attach(true);
		const link = path.join(directory, "alias.ts");
		await fs.symlink(file, link);
		expect(canonicalEditorUri(link)).toBe(canonicalEditorUri(file));
		// Through the alias the dirty buffer is recognized too: disk is written, the unsaved text stays.
		await writeFileWithFallback(link, "via alias\n");
		expect(await Bun.file(file).text()).toBe("via alias\n");
		expect(editorDocument(link)?.text).toBe(snapshot.text);
	});

	test("deleted dirty documents keep their buffer when written through a symlinked parent", async () => {
		attach(true);
		const alias = path.join(directory, "alias");
		await fs.symlink(directory, alias, process.platform === "win32" ? "junction" : "dir");
		await fs.unlink(file);
		const aliasedFile = path.join(alias, path.basename(file));
		expect(canonicalEditorUri(aliasedFile)).toBe(snapshot.uri);
		await writeFileWithFallback(aliasedFile, "recreated");
		expect(await Bun.file(file).text()).toBe("recreated");
		expect(editorDocument(aliasedFile)?.text).toBe(snapshot.text);
	});

	test("missing descendants resolve their nearest existing physical ancestor", async () => {
		const alias = path.join(directory, "alias");
		await fs.symlink(directory, alias, process.platform === "win32" ? "junction" : "dir");
		expect(canonicalEditorUri(path.join(alias, "new", "nested", "file.ts")))
			.toBe(pathToFileURL(path.join(directory, "new", "nested", "file.ts")).href);
	});

	test("dangling links and non-ENOENT canonicalization failures fail closed", async () => {
		const dangling = path.join(directory, "dangling");
		await fs.symlink(path.join(directory, "missing-target"), dangling);
		expect(() => canonicalEditorUri(path.join(dangling, "file.ts"))).toThrow("dangling");
		const loop = path.join(directory, "loop");
		await fs.symlink(loop, loop);
		expect(() => canonicalEditorUri(loop)).toThrow();
		// ENOTDIR must not be mistaken for a valid absent leaf.
		expect(() => canonicalEditorUri(path.join(file, "child.ts"))).toThrow();
	});

	test("an empty overlay registry does not preflight disk metadata", async () => {
		const denied = Object.assign(new Error("metadata denied"), { code: "EACCES" });
		const canonicalize = spyOn(realpathSync, "native").mockImplementation(() => { throw denied; });
		try {
			await writeFileWithFallback(file, "disk only");
			expect(await Bun.file(file).text()).toBe("disk only");
			await deleteFileWithFallback(file);
			expect(canonicalize).not.toHaveBeenCalled();
		} finally { canonicalize.mockRestore(); }
	});

	test("an unresolved path still fails closed while any overlay is registered", async () => {
		attach(true);
		const other = path.join(directory, "opaque.ts");
		await Bun.write(other, "original");
		const denied = Object.assign(new Error("metadata denied"), { code: "EACCES" });
		const canonicalize = spyOn(realpathSync, "native").mockImplementation(() => { throw denied; });
		let brokerCalled = false;
		const disposeWrite = addFileWriteFallback(async () => { brokerCalled = true; return true; });
		const disposeDelete = addFileDeleteFallback(async () => { brokerCalled = true; return true; });
		try {
			await expect(writeFileWithFallback(other, "replacement")).rejects.toBe(denied);
			await expect(deleteFileWithFallback(other)).rejects.toBe(denied);
			expect(brokerCalled).toBe(false);
			expect(await Bun.file(other).text()).toBe("original");
		} finally {
			disposeWrite();
			disposeDelete();
			canonicalize.mockRestore();
		}
		expect(editorDocument(file)?.text).toBe(snapshot.text);
	});

	test.skipIf(process.platform === "win32" || process.getuid?.() === 0)("brokered batches survive an unreadable destination without overlays", async () => {
		const target = await fs.realpath(file);
		const brokered: string[] = [];
		const dispose = addFileWriteFallback(async request => {
			brokered.push(request.dst);
			await fs.chmod(request.dst, 0o600);
			await Bun.write(request.dst, request.content);
			await fs.chmod(request.dst, 0o000);
			return true;
		});
		await fs.chmod(target, 0o000);
		try {
			const write = createLspWritethrough(directory, { enableFormat: false, enableDiagnostics: false });
			const id = `editor-broker-${directory}`;
			await write(target, "brokered", undefined, undefined, { id, flush: false });
			const sibling = path.join(directory, "sibling.ts");
			await write(sibling, "sibling", undefined, undefined, { id, flush: true });
			expect(brokered).toEqual([target]);
			expect(await Bun.file(sibling).text()).toBe("sibling");
		} finally {
			dispose();
			await fs.chmod(target, 0o600);
		}
		expect(await Bun.file(target).text()).toBe("brokered");
	});

	test.skipIf(process.platform === "win32" || process.getuid?.() === 0)("brokered deletes preserve unresolved metadata without overlays", async () => {
		const opaque = path.join(await fs.realpath(directory), "opaque");
		await fs.mkdir(opaque);
		const victim = path.join(opaque, "buried.ts");
		await Bun.write(victim, "original");
		const seen: { dst: string; confirmedFile: boolean }[] = [];
		const dispose = addFileDeleteFallback(async request => {
			seen.push({ dst: request.dst, confirmedFile: request.confirmedFile });
			await fs.chmod(opaque, 0o700);
			await fs.unlink(request.dst);
			return true;
		});
		await fs.chmod(opaque, 0o000);
		try {
			await deleteFileWithFallback(victim);
			expect(seen).toEqual([{ dst: victim, confirmedFile: false }]);
			expect(await Bun.file(victim).exists()).toBe(false);
		} finally {
			dispose();
			await fs.chmod(opaque, 0o700);
		}
	});

	test.skipIf(process.platform === "win32" || process.getuid?.() === 0)("dangling writes keep their filesystem denial and never reach a broker", async () => {
		const locked = path.join(directory, "locked");
		await fs.mkdir(locked);
		const dangling = path.join(locked, "dangling");
		await fs.symlink(path.join(locked, "missing"), dangling);
		let called = false;
		const dispose = addFileWriteFallback(async () => { called = true; return true; });
		await fs.chmod(locked, 0o500);
		try {
			await expect(writeFileWithFallback(dangling, "payload")).rejects.toMatchObject({
				code: expect.stringMatching(/^(EACCES|EPERM)$/),
			});
			expect(called).toBe(false);
		} finally {
			dispose();
			await fs.chmod(locked, 0o700);
		}
	});

	test("LSP forwarding requires current trust and physical project containment", async () => {
		attach(true);
		requireEditorProjectTrust();
		const { client, writes } = recordingClient(directory);
		await expect(syncContent(client, file, snapshot.text)).rejects.toThrow("trust");
		expect(writes).toHaveLength(0);
		setEditorProjectTrust(directory, true, [client.config]);
		await syncContent(client, file, snapshot.text);
		expect(writes).toHaveLength(1);

		const otherRoot = path.join(directory, "project-b");
		await fs.mkdir(otherRoot);
		const other = recordingClient(otherRoot);
		setEditorProjectTrust(otherRoot, true, [other.client.config]);
		try {
			const alias = path.join(otherRoot, "alias-to-a");
			await fs.symlink(directory, alias, process.platform === "win32" ? "junction" : "dir");
			await expect(syncContent(other.client, path.join(alias, path.basename(file)), snapshot.text)).rejects.toThrow("outside");
			other.client.openFiles.set(snapshot.uri, { version: 1, syncedHash: 0, languageId: "typescript" });
			await expect(notifySaved(other.client, file)).rejects.toThrow("outside");
			expect(other.writes).toHaveLength(0);
		} finally { setEditorProjectTrust(otherRoot, false); }

		setEditorProjectTrust(directory, false);
		await expect(syncContent(client, file, "latest unsaved")).rejects.toThrow("trust");
		await expect(notifySaved(client, file)).rejects.toThrow("trust");
		expect(writes).toHaveLength(1);
		expect(editorDocument(file)?.dirty).toBe(true); // revocation never removes write protection
	});

	test("revocation blocks snapshots already queued for an existing LSP client", async () => {
		attach(true);
		requireEditorProjectTrust();
		const { client, writes } = recordingClient(directory);
		setEditorProjectTrust(directory, true, [client.config]);
		const pending = Promise.withResolvers<void>();
		client.writeQueue = pending.promise;
		const sent = sendNotification(client, "textDocument/didChange", {
			textDocument: { uri: snapshot.uri, version: 1 }, contentChanges: [{ text: snapshot.text }],
		});
		setEditorProjectTrust(directory, false);
		pending.resolve();
		await expect(sent).rejects.toThrow("trust");
		expect(writes).toHaveLength(0);
	});

	test("RPC open is lazy and stale changes and requests are rejected", async () => {
		const bridge = new RpcEditorBridge(directory, () => {});
		bridges.push(bridge);
		await bridge.handle({ ...snapshot, type: "editor_document", action: "open", version: 3 });
		await expect(bridge.handle({ ...snapshot, type: "editor_document", action: "change", version: 2 })).rejects.toThrow("Stale");
		await expect(bridge.handle({ ...snapshot, type: "editor_document", action: "request", method: "hover", position: { line: 0, character: 0 } })).rejects.toThrow("Stale");
	});

	test("explicit trust is separate from document opening and binds server configuration", () => {
		requireEditorProjectTrust();
		const config = { command: "typescript-language-server", args: ["--stdio"] };
		expect(() => assertEditorProjectTrusted(directory, config)).toThrow("trust");
		setEditorProjectTrust(directory, true, [config]);
		expect(() => assertEditorProjectTrusted(directory, config)).not.toThrow();
		expect(() => assertEditorProjectTrusted(directory, { ...config, command: "changed-server" })).toThrow("trust");
		setEditorProjectTrust(directory, false);
		expect(() => assertEditorProjectTrusted(directory, config)).toThrow("trust");
	});
});
