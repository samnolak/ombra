import { createHash } from "node:crypto";
import { lstatSync, realpathSync } from "node:fs";
import * as path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export interface EditorDocumentSnapshot {
	uri: string;
	version: number;
	text: string;
	languageId: string;
	baseHash: string;
	dirty: boolean;
	conflict?: boolean;
}
export interface EditorDocument extends EditorDocumentSnapshot {
	owner: object;
	apply: (snapshot: EditorDocumentSnapshot, text: string) => Promise<EditorDocumentSnapshot>;
}

/** Process-wide: every engine session and in-process agent uses the same GUI overlay. */
const documents = new Map<string, EditorDocument>();
let requireTrust = false;
const trustedRoots = new Map<string, Set<string>>();

function canonicalEditorPath(file: string): string {
	try {
		return realpathSync.native(file);
	} catch (error) {
		if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
		// An absent leaf is valid for a deleted/recovered buffer, but dangling
		// links and other filesystem errors must never become lexical aliases.
		try {
			if (lstatSync(file).isSymbolicLink()) throw new Error(`Cannot resolve dangling editor symlink: ${file}`);
		} catch (linkError) {
			if ((linkError as NodeJS.ErrnoException).code !== "ENOENT") throw linkError;
		}
		const parent = path.dirname(file);
		if (parent === file) throw error;
		return path.join(canonicalEditorPath(parent), path.basename(file));
	}
}

export function canonicalEditorUri(value: string): string {
	let file = value.startsWith("file:") ? fileURLToPath(value) : value;
	if (!path.isAbsolute(file)) file = `${process.cwd()}${path.sep}${file}`;
	file = canonicalEditorPath(file);
	if (process.platform === "win32") file = file.toLowerCase();
	return pathToFileURL(file).href;
}
export function requireEditorProjectTrust(required = true): void { requireTrust = required; }
export function setEditorProjectTrust(cwd: string, trusted: boolean, configurations: readonly unknown[] = []): void {
	const root = canonicalEditorUri(cwd);
	if (trusted) trustedRoots.set(root, new Set(configurations.map(config => JSON.stringify(config))));
	else trustedRoots.delete(root);
}
export function assertEditorProjectTrusted(cwd: string, configuration?: unknown): void {
	const grant = trustedRoots.get(canonicalEditorUri(cwd));
	if (requireTrust && (!grant || (configuration !== undefined && !grant.has(JSON.stringify(configuration))))) {
		throw new Error("Explicit project trust is required for this language server configuration.");
	}
}
/** Overlay registration is global; disclosure to a server is project-local and trusted. */
export function assertEditorDocumentTrusted(cwd: string, file: string, configuration?: unknown): void {
	assertEditorProjectTrusted(cwd, configuration);
	const relative = path.relative(fileURLToPath(canonicalEditorUri(cwd)), fileURLToPath(canonicalEditorUri(file)));
	if (relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
		throw new Error("Document is outside the trusted project");
	}
}
export function editorDocument(file: string): EditorDocument | undefined {
	return documents.get(canonicalEditorUri(file));
}
export function validateEditorSnapshot(snapshot: EditorDocumentSnapshot): void {
	if (!Number.isSafeInteger(snapshot.version) || snapshot.version < 1) throw new Error("Invalid editor revision");
	if (typeof snapshot.text !== "string" || typeof snapshot.languageId !== "string" || typeof snapshot.dirty !== "boolean" || !/^[a-f\d]{64}$/i.test(snapshot.baseHash)) {
		throw new Error("Invalid editor document snapshot");
	}
	if (!snapshot.uri.startsWith("file:")) throw new Error("Editor documents require file URIs");
}
export function registerEditorDocument(snapshot: EditorDocumentSnapshot, owner: object, apply: EditorDocument["apply"]): EditorDocument {
	validateEditorSnapshot(snapshot);
	const uri = canonicalEditorUri(snapshot.uri);
	const previous = documents.get(uri);
	if (previous && (snapshot.version < previous.version || (snapshot.version === previous.version && snapshot.text !== previous.text))) {
		throw new Error("Stale editor document revision");
	}
	const document = { ...snapshot, uri, owner, apply };
	documents.set(uri, document);
	return document;
}
export function closeEditorDocument(uri: string, owner: object): void {
	const key = canonicalEditorUri(uri);
	if (documents.get(key)?.owner === owner) documents.delete(key);
}
export function disconnectEditorDocuments(owner: object): void {
	// Keep the guard when a chat disconnects while its GUI buffers remain open.
	for (const document of documents.values()) if (document.owner === owner) {
		document.apply = async () => { throw new Error("Editor disconnected; reopen the document connection before editing."); };
	}
}
export function assertEditorRevision(document: EditorDocument, version: number, baseHash: string): void {
	const current = documents.get(document.uri);
	if (!current || current.version !== version || current.baseHash.toLowerCase() !== baseHash.toLowerCase()) throw new Error("Stale editor revision or base hash");
}
/**
 * The GUI buffer that should receive an agent edit, or undefined when the edit goes to disk instead: no open buffer, or a
 * buffer with unsaved/conflicting user text. Writing disk never touches that text; the editor sees the change and offers
 * "Reload (keeps your text as a copy) / Keep mine".
 */
export function assertEditorWritable(file: string, version?: number, hash?: string): EditorDocument | undefined {
	const document = editorDocument(file);
	if (!document || document.dirty || document.conflict) return undefined;
	if (version !== undefined) assertEditorRevision(document, version, hash ?? "");
	return document;
}
/** Language-server edits are computed against the buffer the server sees, so they cannot fall back to the disk text. */
export function assertNoUnsavedEditorBuffer(file: string): void {
	const document = editorDocument(file);
	if (document?.dirty || document?.conflict) throw new Error(`Unsaved or conflicting editor buffer: ${file}. Save or reload it before language-server edits.`);
}
/** Resource operations cannot preserve the identity/undo history of a live GUI buffer. */
export function assertNoEditorResourceMutation(file: string): void {
	const uri = canonicalEditorUri(file);
	for (const key of documents.keys()) if (key === uri || key.startsWith(`${uri}/`)) {
		throw new Error(`Close the editor document before deleting, replacing or moving ${file}`);
	}
}
/** Return true only after the UI has accepted the revision/hash-checked edit into undo. Never writes disk. */
export async function applyEditorContent(file: string, text: string, expected?: EditorDocumentSnapshot): Promise<boolean> {
	const document = assertEditorWritable(file, expected?.version, expected?.baseHash);
	if (!document) return false;
	if (text === document.text) return true;
	const snapshot = { ...document };
	const diskHash = createHash("sha256").update(await Bun.file(fileURLToPath(document.uri)).bytes()).digest("hex");
	if (diskHash !== snapshot.baseHash.toLowerCase()) throw new Error("Editor base file changed on disk; reload before applying edits");
	if (!assertEditorWritable(file, snapshot.version, snapshot.baseHash)) throw new Error("Editor buffer gained unsaved edits; retry the edit");
	const result = await document.apply(snapshot, text);
	validateEditorSnapshot(result);
	if (canonicalEditorUri(result.uri) !== document.uri || result.text !== text || result.version <= snapshot.version || !result.dirty) {
		throw new Error("Editor rejected or returned an invalid edit acknowledgement");
	}
	registerEditorDocument(result, document.owner, document.apply);
	return true;
}
