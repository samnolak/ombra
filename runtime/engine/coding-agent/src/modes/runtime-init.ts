/**
 * Shared extension runtime wiring for print and RPC modes.
 *
 * Both modes initialize the extension runner with the same action handlers
 * that delegate to the {@link AgentSession}. Only error reporting, shutdown
 * behavior, and UI context differ between callers — those stay as
 * caller-supplied hooks.
 */
import { runExtensionCompact, runExtensionSetModel } from "../extensibility/extensions/compact-handler";
import { getSessionSlashCommands } from "../extensibility/extensions/get-commands-handler";
import type { ExtensionError, ExtensionMode, ExtensionUIContext } from "../extensibility/extensions/types";
import type { AgentSession } from "../session/agent-session";
import { USER_INTERRUPT_LABEL } from "../session/messages";

/** Action name for an extension-originated send failure. */
export type ExtensionSendAction = "extension_send" | "extension_send_user";

export type ExtensionMessageAdmission = "admitted" | "local" | "cancelled";

export interface InitializeExtensionsOptions {
	/** Reports an error thrown by an extension-initiated send. */
	reportSendError: (action: ExtensionSendAction, error: Error) => void;
	/** Reports a runtime error surfaced through {@link ExtensionRunner.onError}. */
	reportRuntimeError: (error: ExtensionError) => void;
	/** Optional shutdown hook (rpc mode signals its loop; print mode is a no-op). */
	onShutdown?: () => void;
	/** Pi-compatible mode exposed to extension contexts. Defaults to `"print"`. */
	mode?: ExtensionMode;
	/** Optional UI context (rpc supplies one; print runs headless). */
	uiContext?: ExtensionUIContext;
	/** Tracks completion separately from causal queue/dispatch admission. */
	trackAgentInvokingMessage?: (task: Promise<unknown>, admission: Promise<ExtensionMessageAdmission>) => void;
	/** Optional observer of every extension-originated send, turn-triggering or not. */
	trackExtensionSend?: (task: Promise<unknown>) => void;
	/** Optional filter applied to tool names an extension activates. */
	filterActiveTools?: (toolNames: string[]) => string[];
	/**
	 * Optional wrapper around extension-initiated session changes (new, branch,
	 * navigate, switch, reload), so the host can quiesce and reattach its own per-session
	 * state exactly as it does for its own session-change commands.
	 * `detachesRun` is true for changes that stop the running agent (new, switch);
	 * branch and navigation leave a live run streaming to its normal end.
	 */
	wrapSessionChange?: <T extends { cancelled: boolean }>(
		change: () => Promise<T>,
		options: { detachesRun: boolean },
	) => Promise<T>;
}

/**
 * Initialize the session's extension runner with the standard action set
 * shared by non-interactive modes, then emit `session_start`.
 *
 * No-op when the session was constructed without an extension runner.
 */
export async function initializeExtensions(session: AgentSession, options: InitializeExtensionsOptions): Promise<void> {
	const runner = session.extensionRunner;
	if (!runner) return;

	const {
		reportSendError,
		reportRuntimeError,
		onShutdown,
		mode = "print",
		uiContext,
		trackAgentInvokingMessage,
		trackExtensionSend,
		filterActiveTools,
		wrapSessionChange = change => change(),
	} = options;
	const shutdown = onShutdown ?? (() => {});

	runner.initialize(
		// ExtensionActions
		{
			sendMessage: (message, sendOptions) => {
				const admission = Promise.withResolvers<ExtensionMessageAdmission>();
				const sendTask = session.sendCustomMessage(message, {
					...sendOptions,
					onPromptAdmitted: () => admission.resolve("admitted"),
					onPromptCancelled: () => admission.resolve("cancelled"),
				});
				trackExtensionSend?.(sendTask);
				trackAgentInvokingMessage?.(sendTask, admission.promise);
				// Appending/folding a message is local; cancelled setup reports separately.
				// A successful full turn must never be the acknowledgement barrier.
				void sendTask.then(() => admission.resolve("local"), admission.reject);
				void admission.promise.catch(() => {});
				void sendTask.catch(e => {
					reportSendError("extension_send", e instanceof Error ? e : new Error(String(e)));
				});
			},
			sendUserMessage: (content, sendOptions) => {
				const admission = Promise.withResolvers<ExtensionMessageAdmission>();
				const sendTask = session.sendUserMessage(content, {
					...sendOptions,
					onPromptAdmitted: () => admission.resolve("admitted"),
				});
				trackExtensionSend?.(sendTask);
				trackAgentInvokingMessage?.(sendTask, admission.promise);
				void sendTask.then(() => admission.resolve("cancelled"), admission.reject);
				void admission.promise.catch(() => {});
				void sendTask.catch(e => {
					reportSendError("extension_send_user", e instanceof Error ? e : new Error(String(e)));
				});
			},
			appendEntry: (customType, data) => {
				session.sessionManager.appendCustomEntry(customType, data);
			},
			setLabel: (targetId, label) => {
				session.sessionManager.appendLabelChange(targetId, label);
			},
			getActiveTools: () => session.getEnabledToolNames(),
			getAllTools: () => session.getAllToolInfos(),
			setActiveTools: (toolNames: string[]) =>
				session.setActiveToolsByName(filterActiveTools ? filterActiveTools(toolNames) : toolNames),
			getCommands: () => getSessionSlashCommands(session),
			setModel: model => runExtensionSetModel(session, model),
			getThinkingLevel: () => session.thinkingLevel,
			setThinkingLevel: level => session.setThinkingLevel(level),
			getServiceTiers: () => session.serviceTierByFamily,
			setServiceTier: (family, tier) => session.setServiceTierFamily(family, tier),
			getSessionName: () => session.sessionManager.getSessionName(),
			setSessionName: async name => {
				await session.sessionManager.setSessionName(name, "user");
			},
		},
		// ExtensionContextActions
		{
			getModel: () => session.model,
			isIdle: () => !session.isStreaming,
			abort: () => session.abort({ reason: USER_INTERRUPT_LABEL }),
			hasPendingMessages: () => session.queuedMessageCount > 0,
			shutdown,
			getContextUsage: () => session.getContextUsage(),
			getSystemPrompt: () => session.systemPrompt,
			runEphemeralTurn: args => session.runEphemeralTurn(args),
			compact: instructionsOrOptions => runExtensionCompact(session, instructionsOrOptions),
		},
		// ExtensionCommandContextActions — commands invokable via prompt("/command")
		{
			getContextUsage: () => session.getContextUsage(),
			waitForIdle: () => session.agent.waitForIdle(),
			newSession: newOptions =>
				wrapSessionChange(
					async () => {
						const success = await session.newSession({ parentSession: newOptions?.parentSession });
						if (success && newOptions?.setup) {
							await newOptions.setup(session.sessionManager);
						}
						return { cancelled: !success };
					},
					{ detachesRun: true },
				),
			branch: entryId =>
				wrapSessionChange(
					async () => {
						const result = await session.branch(entryId);
						return { cancelled: result.cancelled };
					},
					{ detachesRun: false },
				),
			navigateTree: (targetId, navOptions) =>
				wrapSessionChange(
					async () => {
						const result = await session.navigateTree(targetId, { summarize: navOptions?.summarize });
						return { cancelled: result.cancelled };
					},
					{ detachesRun: false },
				),
			switchSession: sessionPath =>
				wrapSessionChange(
					async () => {
						const success = await session.switchSession(sessionPath);
						return { cancelled: !success };
					},
					{ detachesRun: true },
				),
			// Reload reopens the session file (as `session.reload()` does), detaching a live run;
			// it throws when cancelled, after the wrapper has seen the change as cancelled.
			reload: async () => {
				const result = await wrapSessionChange(
					async () => {
						// Without a session file reload is a no-op and nothing is detached.
						const sessionFile = session.sessionFile;
						if (!sessionFile) return { cancelled: true };
						return { cancelled: !(await session.switchSession(sessionFile)) };
					},
					{ detachesRun: true },
				);
				if (result.cancelled && session.sessionFile) throw new Error("Session reload cancelled");
			},
			compact: instructionsOrOptions => runExtensionCompact(session, instructionsOrOptions),
		},
		uiContext,
		mode,
	);

	runner.onError(reportRuntimeError);
	await runner.emit({ type: "session_start" });
}
