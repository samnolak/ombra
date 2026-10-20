#!/usr/bin/env bun
import { Console } from "node:console";
import { Writable } from "node:stream";
import type { RpcSessionRuntime } from "./coding-agent/src/modes/rpc/rpc-mode";
import { requireEditorProjectTrust } from "./coding-agent/src/lsp/editor-documents";
import { version as ompVersion } from "./coding-agent/package.json";

// One immutable process profile/environment for the whole host. Establish it
// before importing SDK modules, whose environment/profile caches are shared.
process.env.PI_NOTIFICATIONS = "off";
delete process.env.MallocStackLogging;
delete process.env.MallocStackLoggingNoCompact;

// Keep the runtime's original stdout writer: a second fs.WriteStream over fd 1
// fails under large writes on pinned Bun. The private wrapper retains normal
// backpressure while extension stdout writes remain diagnostics.
const writeProtocol = process.stdout.write.bind(process.stdout);
const protocolOutput = new Writable({
	decodeStrings: false,
	write(chunk, encoding, callback) { writeProtocol(chunk, encoding, callback); },
});
process.stdout.on("error", error => protocolOutput.destroy(error));
globalThis.console = new Console({ stdout: process.stderr, stderr: process.stderr });
process.stdout.write = process.stderr.write.bind(process.stderr);

const args = process.argv.slice(2);
const selfCheck = args.length === 1 && args[0] === "--self-check";
if (args.length && !selfCheck && !(args.length === 2 && args[0] === "--mode" && args[1] === "engine")) {
	throw new Error("Engine host accepts no session arguments; send them in engine_open.arguments");
}
const { resolveProfileEnv, setProfile } = await import("@oh-my-pi/pi-utils/dirs");
setProfile(resolveProfileEnv(process.env.OMP_PROFILE, process.env.PI_PROFILE));
const { installGlobalProxyFetch } = await import("@oh-my-pi/pi-ai/utils/proxy");
installGlobalProxyFetch();
const { claimRpcInput } = await import("./coding-agent/src/modes/rpc/rpc-input");
// Claim input before extension discovery can obtain Bun's singleton reader.
const input = selfCheck ? undefined : claimRpcInput();
requireEditorProjectTrust();
const [{ createEngineSession, initEngineProcessSettings }, { createRpcSessionRuntime }, { EngineHost, readEngineInput }, { RpcFrameEncoder }, { RpcOutputWriter }, { setSessionLeaseIdentity }, { setMcpOAuthClientName }] = await Promise.all([
	import("./session-factory"),
	import("./coding-agent/src/modes/rpc/rpc-mode"),
	import("./engine-host"),
	import("./coding-agent/src/modes/rpc/rpc-frame"),
	import("./coding-agent/src/modes/rpc/rpc-output"),
	import("./coding-agent/src/session/session-storage"),
	import("./coding-agent/src/mcp/oauth-flow"),
]);
if (selfCheck) {
	const sent = Promise.withResolvers<void>();
	protocolOutput.write(
		`${JSON.stringify({ engineProtocolVersion: 1, ompVersion })}\n`,
		error => error ? sent.reject(error) : sent.resolve(),
	);
	await sent.promise;
	process.exit(0);
}
await initEngineProcessSettings();
// Lease holder records name this process, so another window or a terminal can say where a chat is open.
setSessionLeaseIdentity("ombra", "Ombra");
// MCP dynamic client registration names Ombra unless a server entry sets oauth.clientName.
setMcpOAuthClientName("Ombra");

const encoder = new RpcFrameEncoder();
let fatalOutputError: Error | undefined;
const writer = new RpcOutputWriter(protocolOutput, error => {
	fatalOutputError = error;
	process.stderr.write(`Engine output failed: ${error.message}\n`);
	void host.shutdown().finally(() => process.exit(1));
});
const host = new EngineHost({
	output: frame => writer.write(encoder.encodeFrames(frame)),
	negotiate: () => encoder.setProtocolVersion(2),
	createSession: async (cwd, arguments_, output, envelopeBytes, publish, resumeOnCurrentModel) => {
		const created = await createEngineSession(cwd, arguments_, resumeOnCurrentModel);
		let scopedRuntime: RpcSessionRuntime | undefined;
		try {
			await created.runInContext(() => createRpcSessionRuntime(created.session, {
				output, envelopeBytes, protocolVersion: 2,
				setToolUIContext: created.setToolUIContext,
				subagentEventBus: created.subagentEventBus,
				mcpManager: created.mcpManager,
				headless: arguments_.includes("--no-ui"),
				onRuntime: runtime => {
					scopedRuntime = {
						dispatch: (frame, processed) => created.runInContext(() => runtime.dispatch(frame, processed)),
						close: () => created.runInContext(() => runtime.close()),
						closed: runtime.closed,
					};
					publish(scopedRuntime);
				},
			}));
			created.runInContext(() => created.startBackgroundModelDiscovery?.());
			if (!scopedRuntime) throw new Error("RPC runtime did not publish its dispatcher");
			return scopedRuntime;
		} catch (error) {
			await (scopedRuntime ? scopedRuntime.close() : created.runInContext(() => created.session.dispose())).catch(disposeError => {
				process.stderr.write(`Engine session startup cleanup failed: ${String(disposeError)}\n`);
			});
			throw error;
		}
	},
});
const terminate = async (error?: unknown): Promise<never> => {
	let failed = Boolean(error || fatalOutputError);
	if (error) process.stderr.write(`Engine stopped: ${String(error)}\n`);
	try { await host.shutdown(); } catch (shutdownError) {
		failed = true;
		process.stderr.write(`Engine shutdown failed: ${String(shutdownError)}\n`);
	}
	try { await writer.close(); } catch { failed = true; }
	process.exit(failed ? 1 : 0);
};
process.once("SIGTERM", () => { void terminate(); });
process.once("SIGINT", () => { void terminate(); });
host.start();
// An explicit shutdown can complete while stdin remains open. Exit only after
// the root response and all queued logical close frames have been delivered.
void host.closed.then(
	() => queueMicrotask(() => { void terminate(); }),
	error => queueMicrotask(() => { void terminate(error); }),
);
try {
	await readEngineInput(input!, (frame, bytes) => host.dispatch(frame, bytes), error => {
		writer.write(encoder.encodeFrames({ type: "response", command: "parse", success: false, error }));
	});
	await terminate();
} catch (error) {
	await terminate(error);
}
