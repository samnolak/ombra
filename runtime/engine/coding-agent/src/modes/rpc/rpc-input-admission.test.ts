import { describe, expect, test } from "bun:test";
import { dispatchRpcInputFrame, type PendingExtensionRequest, type RpcInputFrameDeps } from "./rpc-mode";
import type { RpcResponse } from "./rpc-types";

describe("RPC input admission does not block control commands", () => {
	for (const inputType of ["prompt", "steer", "follow_up", "abort_and_prompt"] as const) {
		test(`${inputType} allows abort to overtake unfinished preparation`, async () => {
			const inputResponse = Promise.withResolvers<RpcResponse>();
			const handled: string[] = [];
			const output: object[] = [];
			const background: Promise<void>[] = [];
			const deps: RpcInputFrameDeps = {
				handleCommand: async command => {
					handled.push(command.type);
					if (command.type === inputType) return inputResponse.promise;
					return { type: "response", id: command.id, command: "abort", success: true };
				},
				output: frame => output.push(frame),
				errorResponse: (id, command, error) => ({ type: "response", id, command, success: false, error }),
				trackBackgroundTask: task => background.push(task),
				pendingExtensionRequests: new Map<string, PendingExtensionRequest>(),
				onHostToolResult: () => {},
				onHostToolUpdate: () => {},
				onHostUriResult: () => {},
			};
			const input = dispatchRpcInputFrame({ type: inputType, id: "input", message: "prepare slowly" }, deps);
			expect(input).toBeUndefined();
			expect(background).toHaveLength(1);
			await dispatchRpcInputFrame({ type: "abort", id: "abort" }, deps);
			expect(handled).toEqual([inputType, "abort"]);
			expect(output).toEqual([{ type: "response", id: "abort", command: "abort", success: true }]);
			inputResponse.resolve({
				type: "response",
				id: "input",
				command: inputType,
				success: true,
				data: { inputDisposition: "cancelled", agentInvoked: false },
			});
			await Promise.all(background);
			expect(output[1]).toMatchObject({
				command: inputType,
				data: { inputDisposition: "cancelled", agentInvoked: false },
			});
		});
	}
});
