import { createInterface } from "node:readline";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

// A real canonical SessionManager in a separate pinned-Bun process, not a storage mock.
const root = process.env.OMBRA_ENGINE_ROOT;
if (!root) throw new Error("OMBRA_ENGINE_ROOT must name the staged engine source with pinned dependencies");
const { SessionManager } = await import(pathToFileURL(join(root, "coding-agent/src/session/session-manager.ts")).href);
const { FileSessionStorage } = await import(pathToFileURL(join(root, "coding-agent/src/session/session-storage.ts")).href);
const storage = new FileSessionStorage();
const manager = await SessionManager.open(process.argv[2], undefined, storage, { throwIfMissing: true, suppressBreadcrumb: true });
const notices: object[] = [];
manager.onPersistenceNotice((notice: object) => notices.push(notice));
const send = (value: object) => process.stdout.write(`${JSON.stringify(value)}\n`);
send({ type: "ready" });
for await (const line of createInterface({ input: process.stdin })) {
	const command = JSON.parse(line);
	try {
		let claimedDuringClose: boolean | undefined;
		switch (command.type) {
			case "append": manager.appendCustomEntry("ownership-test", { marker: command.marker }); await manager.flush(); break;
			case "rename": await manager.setSessionName(command.name, "user"); await manager.flush(); break;
			case "move": await manager.moveTo(command.cwd, command.directory); break;
			case "close": await manager.close(); break;
			case "close_probe": {
				const closing = manager.close();
				const release = storage.claimSession(manager.getSessionId(), manager.getSessionFile());
				claimedDuringClose = release !== null;
				release?.();
				await closing;
				break;
			}
			case "delete": await storage.deleteSessionWithArtifacts(command.path); break;
			case "drop": await manager.dropSession(command.path); break;
			case "read": break;
			default: throw new Error(`Unknown command: ${command.type}`);
		}
		send({ id: command.id, success: true, path: manager.getSessionFile(), sessionId: manager.getSessionId(), notices, claimedDuringClose,
			entries: command.type === "read" ? manager.getEntries() : undefined });
	} catch (error) {
		send({ id: command.id, success: false, error: String(error), path: manager.getSessionFile(), notices });
	}
}
await manager.close();
