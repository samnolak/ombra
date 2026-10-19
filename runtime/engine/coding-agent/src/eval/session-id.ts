import type { ToolSession } from "../tools";

export type EvalSessionSource = Pick<ToolSession, "cwd" | "getSessionFile" | "getEvalKernelOwnerId">;

export function defaultEvalSessionId(session: EvalSessionSource): string {
	const sessionFile = session.getSessionFile?.() ?? undefined;
	const identity = sessionFile ? `session:${sessionFile}:cwd:${session.cwd}` : `cwd:${session.cwd}`;
	const owner = session.getEvalKernelOwnerId?.();
	return owner ? `${identity}:owner:${owner}` : identity;
}
