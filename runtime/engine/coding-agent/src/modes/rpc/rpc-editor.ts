import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { applyWorkspaceEditWithLsp, getEditorLspClients, getOrCreateClient, notifySaved, sendNotification, sendRequest, syncContent } from "../../lsp/client";
import { getConfig, getServersForFile } from "../../lsp/config";
import { waitForDiagnostics } from "../../lsp/diagnostics";
import { assertEditorDocumentTrusted, assertEditorRevision, canonicalEditorUri, closeEditorDocument, disconnectEditorDocuments, editorDocument, registerEditorDocument, setEditorProjectTrust, type EditorDocumentSnapshot } from "../../lsp/editor-documents";
import type { WorkspaceEdit } from "../../lsp/types";

export interface EditorDocumentCommand extends Omit<EditorDocumentSnapshot, "text" | "languageId" | "dirty" | "baseHash"> {
	id?: string;
	type: "editor_document";
	action: "open" | "change" | "save" | "close" | "request";
	text?: string;
	languageId?: string;
	baseHash?: string;
	dirty?: boolean;
	method?: "completion" | "hover" | "definition" | "rename" | "diagnostics";
	position?: { line: number; character: number };
	newName?: string;
}
export type EditorCommand = EditorDocumentCommand
	| { id?: string; type: "editor_trust"; trusted: boolean }
	| { id?: string; type: "editor_edit_result"; requestId: string; success: boolean; error?: string; document?: EditorDocumentSnapshot };

/** One session transport, but document ownership and LSP clients are process-wide. */
export class RpcEditorBridge {
	readonly #pending = new Map<string, { resolve: (value: EditorDocumentSnapshot) => void; reject: (error: Error) => void }>();
	#trusted = false;
	constructor(readonly cwd: string, readonly output: (frame: object) => void) {}

	async handle(command: EditorCommand): Promise<object> {
		if (command.type === "editor_edit_result") {
			const pending = this.#pending.get(command.requestId);
			if (pending) {
				this.#pending.delete(command.requestId);
				if (command.success && command.document) pending.resolve(command.document);
				else pending.reject(new Error(command.error ?? "Editor refused the edit"));
			}
			return {};
		}
		if (command.type === "editor_trust") {
			if (typeof command.trusted !== "boolean") throw new Error("trusted must be explicit boolean");
			setEditorProjectTrust(this.cwd, command.trusted, command.trusted ? Object.values(getConfig(this.cwd).servers) : []);
			this.#trusted = command.trusted;
			return { trusted: command.trusted };
		}
		if (!Number.isSafeInteger(command.version) || command.version < 1 || typeof command.uri !== "string" || !command.uri.startsWith("file:")) throw new Error("Invalid editor URI or revision");
		const uri = canonicalEditorUri(command.uri);
		let document = editorDocument(uri);
		if (command.action === "open" || command.action === "change") {
			if (command.action === "change" && (!document || command.version < document.version)) throw new Error("Stale editor change");
			document = registerEditorDocument(command as EditorDocumentSnapshot, this, (snapshot, text) => this.#apply(snapshot, text));
			// Synchronize only running clients here. Opening/typing never starts a process.
			if (this.#trusted) for (const client of getEditorLspClients(this.cwd, fileURLToPath(uri))) await syncContent(client, fileURLToPath(uri), document.text, undefined, document.version, document.languageId);
			return { version: document.version, result: null, positionEncoding: "utf-16" };
		}
		if (!document) throw new Error("Editor document is not open");
		assertEditorRevision(document, command.version, command.action === "save" ? document.baseHash : command.baseHash ?? document.baseHash);
		if (command.action === "close") {
			closeEditorDocument(uri, this);
			if (!editorDocument(uri)) for (const client of getEditorLspClients(this.cwd)) {
				await sendNotification(client, "textDocument/didClose", { textDocument: { uri } });
				client.openFiles.delete(uri);
				client.diagnostics.delete(uri);
			}
			return { version: command.version, result: null, positionEncoding: "utf-16" };
		}
		if (command.action === "save") {
			if (command.dirty !== false || !command.baseHash || !/^[a-f\d]{64}$/i.test(command.baseHash)) throw new Error("Invalid save snapshot");
			document = registerEditorDocument({ ...document, baseHash: command.baseHash, dirty: false, conflict: false }, this, document.apply);
			if (this.#trusted) for (const client of getEditorLspClients(this.cwd, fileURLToPath(uri))) await notifySaved(client, fileURLToPath(uri));
			return { version: command.version, result: null, positionEncoding: "utf-16" };
		}
		if (command.action !== "request") throw new Error("Unknown editor document action");
		if (!this.#trusted) throw new Error("Explicit project trust is required for language services");
		const file = fileURLToPath(uri);
		assertEditorDocumentTrusted(this.cwd, file);
		const methods: Record<string, string> = { completion: "textDocument/completion", hover: "textDocument/hover", definition: "textDocument/definition", rename: "textDocument/rename", diagnostics: "textDocument/diagnostic" };
		if (!command.method || !Object.hasOwn(methods, command.method)) throw new Error("Unsupported editor request");
		if (command.method !== "diagnostics") {
			const position = command.position;
			if (!position || !Number.isSafeInteger(position.line) || !Number.isSafeInteger(position.character) || position.line < 0 || position.character < 0) throw new Error("Invalid UTF-16 position");
			const line = document.text.split("\n")[position.line];
			if (line === undefined || position.character > line.replace(/\r$/, "").length) throw new Error("Position outside document");
		}
		if (command.method === "rename" && (!command.newName || typeof command.newName !== "string")) throw new Error("Rename requires newName");
		const server = getServersForFile(getConfig(this.cwd), file).find(([, config]) => !config.isLinter && !config.createClient);
		if (!server) throw new Error(`No language server configured for ${document.languageId}`);
		const client = await getOrCreateClient(server[1], this.cwd);
		assertEditorRevision(document, command.version, document.baseHash);
		await syncContent(client, file, document.text, undefined, document.version, document.languageId);
		let result: unknown;
		if (command.method === "diagnostics") {
			result = await waitForDiagnostics(client, uri, { expectedDocumentVersion: command.version, timeoutMs: 5000 });
		} else {
			result = await sendRequest(client, methods[command.method]!, { textDocument: { uri }, position: command.position, ...(command.method === "rename" ? { newName: command.newName } : {}) });
		}
		assertEditorRevision(document, command.version, document.baseHash);
		if (command.method === "rename" && result) await applyWorkspaceEditWithLsp(result as WorkspaceEdit, this.cwd);
		return { version: command.version, result, positionEncoding: "utf-16" };
	}

	async #apply(document: EditorDocumentSnapshot, text: string): Promise<EditorDocumentSnapshot> {
		const requestId = randomUUID();
		const { promise, resolve, reject } = Promise.withResolvers<EditorDocumentSnapshot>();
		this.#pending.set(requestId, { resolve, reject });
		const timer = setTimeout(() => { this.#pending.delete(requestId); reject(new Error("Editor edit acknowledgement timed out")); }, 30_000);
		try {
			this.output({ type: "editor_apply_edit", requestId, uri: document.uri, expectedRevision: document.version, expectedHash: document.baseHash, text });
			return await promise;
		} finally { clearTimeout(timer); this.#pending.delete(requestId); }
	}
	close(): void {
		this.#trusted = false;
		setEditorProjectTrust(this.cwd, false);
		disconnectEditorDocuments(this);
		for (const pending of this.#pending.values()) pending.reject(new Error("Editor transport closed"));
		this.#pending.clear();
	}
}
