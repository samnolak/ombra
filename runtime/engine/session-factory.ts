import * as path from "node:path";
import { getAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { type Args, parseArgs } from "./coding-agent/src/cli/args";
import { applyExtensionFlags } from "./coding-agent/src/cli/extension-flags";
import { cfgModelRoles } from "./coding-agent/src/config/model-settings";
import { DEFAULT_PREWALK_TARGET, expandRoleAlias, resolveConfiguredModelPatterns } from "./coding-agent/src/config/model-resolver";
import { all } from "./coding-agent/src/config/registry";
import { serviceTierSettingToTier } from "./coding-agent/src/config/service-tier";
import { Settings } from "./coding-agent/src/config/settings";
import { withActiveSettings } from "./coding-agent/src/config/settings-context";
import { collectUntrustedProjectItems } from "./coding-agent/src/config/untrusted-project";
import { cfgSkillsIncludeSkills } from "./coding-agent/src/extensibility/settings";
import { AgentRegistry, createAgentSession, type CreateAgentSessionResult } from "./coding-agent/src/sdk";
import { SessionManager } from "./coding-agent/src/session/session-manager";
import { SessionLeaseHeldError } from "./coding-agent/src/session/session-lease-error";
import { setUntrustedProjectReport } from "./coding-agent/src/session/untrusted-project-report";
import { cfgWorkspaceAdditionalDirectories } from "./coding-agent/src/session/context-settings";
import { cfgPrewalkEnabled } from "./coding-agent/src/session/settings";
import { discoverTitleSystemPromptFile, loadSystemPromptTemplateFile, resolvePromptInput } from "./coding-agent/src/system-prompt";
import { cfgToolsApprovalMode } from "./coding-agent/src/tools/settings";
import { EventBus } from "./coding-agent/src/utils/event-bus";

export type EngineSessionResult = CreateAgentSessionResult;

// These are parsed CLI fields, not another argv parser. Process bootstrap,
// terminal commands and launch prompts cannot be applied to a single SDK chat.
const unsupportedFields: Partial<Record<keyof Args, string>> = {
	profile: "--profile", alias: "--alias", help: "--help", version: "--version",
	print: "--print", printThoughts: "--print-thoughts", export: "--export",
	fromClaude: "--from-claude", fromCodex: "--from-codex", pluginDirs: "--plugin-dir",
	goal: "--goal",
	planYolo: "--plan-yolo", planYoloInto: "--plan-yolo-into", noPty: "--no-pty",
};

/** Parse real CLI session flags without executing CLI bootstrap or changing cwd/env. */
export function parseEngineSessionArgs(args: readonly string[]): Args {
	const parsed = parseArgs(args);
	for (const [field, flag] of Object.entries(unsupportedFields)) {
		if (parsed[field as keyof Args] !== undefined) {
			throw new Error(`${flag} is a process/interactive launch option, not a shared-engine session option`);
		}
	}
	if (parsed.mode !== undefined && parsed.mode !== "rpc" && parsed.mode !== "rpc-ui") {
		throw new Error("Shared-engine sessions only support RPC mode");
	}
	if (parsed.resume === true) throw new Error("--resume requires a session file or id in the shared engine");
	if (parsed.noSession && (parsed.resume || parsed.continue || parsed.fork)) {
		throw new Error("--no-session cannot be combined with resume, continue, or fork");
	}
	if (parsed.noPrewalk && (parsed.prewalk || parsed.prewalkInto !== undefined)) {
		throw new Error("--no-prewalk cannot be combined with --prewalk or --prewalk-into");
	}
	if (parsed.apiKey && !parsed.model && !parsed.provider && !parsed.models?.length) {
		throw new Error("--api-key requires --model, --provider, or --models");
	}
	return parsed;
}

/**
 * The shared engine's process-wide settings: they belong to no chat, so they are the user's own only (pinned to the
 * agent folder) and fail closed — code that runs outside a chat's settings scope sees an untrusted folder. Created
 * lazily they would read the project layer of the engine's cwd or a `/move` target. Each chat reads its own folder
 * through its own settings (createEngineSession).
 */
export async function initEngineProcessSettings(): Promise<Settings> {
	const agentDir = getAgentDir();
	return Settings.init({ cwd: agentDir, agentDir, projectRoot: agentDir, ignoreProjectSettings: true });
}

export async function createEngineSession(cwd: string, args: readonly string[], resumeOnCurrentModel = false): Promise<EngineSessionResult> {
	const parsed = parseEngineSessionArgs(args);
	const launchCwd = path.resolve(cwd, parsed.cwd ?? ".");
	const sessionDir = parsed.sessionDir ? path.resolve(launchCwd, parsed.sessionDir) : undefined;
	let sessionManager: SessionManager;
	const selected = parsed.fork ?? (typeof parsed.resume === "string" ? parsed.resume : undefined);
	if (selected) {
		let sessionFile: string;
		if (selected.includes("/") || selected.includes("\\") || selected.endsWith(".jsonl")) {
			sessionFile = path.resolve(launchCwd, selected);
		} else {
			const sessions = sessionDir ? await SessionManager.list(launchCwd, sessionDir) : await SessionManager.listAll();
			const matches = sessions.filter(session => session.id === selected || session.id.startsWith(selected));
			if (matches.length !== 1) throw new Error(matches.length ? `Ambiguous session id: ${selected}` : `Session not found: ${selected}`);
			sessionFile = matches[0].path;
		}
		sessionManager = parsed.fork
			? await SessionManager.forkFrom(sessionFile, launchCwd, sessionDir)
			: await SessionManager.open(sessionFile, sessionDir, undefined, { initialCwd: launchCwd, throwIfMissing: true, suppressBreadcrumb: true });
	} else if (parsed.noSession) {
		sessionManager = SessionManager.inMemory(launchCwd);
	} else if (parsed.continue) {
		sessionManager = await SessionManager.continueRecent(launchCwd, sessionDir);
	} else {
		sessionManager = SessionManager.create(launchCwd, sessionDir);
	}

	// A restored chat claims its writer lease before anything else runs: while another
	// window or terminal writes it, the client shows it read-only and may ask for it.
	if ((selected && !parsed.fork) || parsed.continue) {
		let owned: boolean;
		try {
			owned = sessionManager.claimWriterLease();
		} catch (error) {
			sessionManager.seal();
			await sessionManager.close();
			throw error;
		}
		if (!owned) {
			const sessionId = sessionManager.getSessionId();
			const holder = sessionManager.sessionLeaseHolder();
			// Nothing of this reader may reach the journal, not even close-time bookkeeping.
			sessionManager.seal();
			await sessionManager.close();
			throw new SessionLeaseHeldError(sessionId, holder);
		}
	}

	let created: CreateAgentSessionResult | undefined;
	try {
		const projectCwd = sessionManager.getCwd();
		const settings = await Settings.loadIsolated({
			cwd: projectCwd,
			agentDir: getAgentDir(),
			configFiles: parsed.config?.map(file => path.resolve(launchCwd, file)),
			// An untrusted folder's own settings and MCP servers never load; the user's own still do.
			ignoreProjectSettings: parsed.untrustedProject,
			// A moved session never takes on another folder's settings: the client restarts it under that folder's trust.
			projectRoot: projectCwd,
			overrides: {
				"hideThinkingBlock": parsed.hideThinking,
				"advisor.enabled": parsed.advisor,
				"externalThinking": parsed.externalThinking,
				"prewalk.enabled": parsed.noPrewalk ? false : undefined,
				"mcp.enableProjectConfig": parsed.untrustedProject ? false : undefined,
			},
		});
		for (const setting of all()) {
			if (setting.definition.protocolDefault?.includes("rpc")) setting.pinDefault(settings);
		}
		if (parsed.approvalMode || parsed.autoApprove) {
			cfgToolsApprovalMode.override(settings, parsed.approvalMode ?? "yolo");
		}
		if (parsed.skills) cfgSkillsIncludeSkills.override(settings, parsed.skills);
		if (parsed.smol || parsed.slow || parsed.plan) {
			cfgModelRoles.override(settings, {
				...cfgModelRoles.get(settings),
				...(parsed.smol ? { smol: parsed.smol } : {}),
				...(parsed.slow ? { slow: parsed.slow } : {}),
				...(parsed.plan ? { plan: parsed.plan } : {}),
			});
		}
		const registry = new AgentRegistry();
		created = await AgentRegistry.runWithRegistry(registry, () => withActiveSettings(settings, async () => {
			const [customSystemPrompt, appendSystemPrompt, systemPromptTemplate, titleSystemPrompt] = await Promise.all([
				resolvePromptInput(parsed.systemPrompt, "system prompt", projectCwd),
				resolvePromptInput(parsed.appendSystemPrompt, "append system prompt", projectCwd),
				parsed.systemPromptTemplate ? loadSystemPromptTemplateFile(path.resolve(projectCwd, parsed.systemPromptTemplate)) : undefined,
				resolvePromptInput(discoverTitleSystemPromptFile(projectCwd), "title system prompt", projectCwd),
			]);
			// Launch preferences apply to new chats, not restored history. Only the
			// explicit recovery action may replace a saved model (including with the settings default).
			const launchModelPattern = parsed.model
				? parsed.provider ? `${parsed.provider}/${parsed.model}` : parsed.model
				: parsed.models?.length ? parsed.models : parsed.provider ? `${parsed.provider}/*` : undefined;
			const restoring = Boolean(selected || parsed.continue);
			const modelPattern = restoring
				? resumeOnCurrentModel ? launchModelPattern ?? "default" : undefined
				: launchModelPattern;
			const prewalkEnabled = !parsed.noPrewalk && (
				parsed.prewalk || parsed.prewalkInto !== undefined ||
				(!parsed.resume && !parsed.continue && cfgPrewalkEnabled.get(settings))
			);
			const prewalkTarget = parsed.prewalkInto ?? DEFAULT_PREWALK_TARGET;
			const prewalkSelector = prewalkTarget.trim() === "default"
				? expandRoleAlias(prewalkTarget, settings) : prewalkTarget;
			const prewalkPatterns = prewalkEnabled ? resolveConfiguredModelPatterns(prewalkSelector, settings) : [];
			return createAgentSession({
				cwd: projectCwd, settings, sessionManager, agentRegistry: registry,
				eventBus: new EventBus(), subagentEventBus: new EventBus(), bindProcessState: false,
				hasUI: true, interactivePrompts: true, allowSessionModelFallback: false,
				// The GUI shows MCP forms (rpc-mcp.ts attaches the handler once the RPC runtime exists).
				mcpElicitation: true,
				deferUsageReserveConfirmation: true,
				modelPattern, thinkingLevel: parsed.thinking,
				deferredPrewalk: prewalkEnabled
					? { target: prewalkTarget, patterns: prewalkPatterns.length ? prewalkPatterns : [prewalkSelector] }
					: undefined,
				autoApprove: parsed.autoApprove,
				additionalDirectories: [...new Set([...cfgWorkspaceAdditionalDirectories.get(settings), ...(parsed.addDir ?? []).map(dir => path.resolve(projectCwd, dir))])],
				additionalExtensionPaths: [...(parsed.trustedExtensions ?? []), ...(parsed.extensions ?? []), ...(parsed.hooks ?? [])].map(file => path.resolve(projectCwd, file)),
				disableExtensionDiscovery: parsed.noExtensions,
				disableProjectCustomTools: parsed.untrustedProject,
				toolNames: parsed.noTools ? parsed.tools ?? [] : parsed.tools,
				enableLsp: parsed.noLsp ? false : undefined,
				skills: parsed.noSkills ? [] : undefined, rules: parsed.noRules ? [] : undefined,
				customSystemPrompt, appendSystemPrompt, systemPromptTemplate, titleSystemPrompt,
				providerSessionId: parsed.providerSessionId,
				providerPromptCacheKey: parsed.providerPromptCacheKey,
				providerPromptCacheKeySource: parsed.providerPromptCacheKey ? "explicit" : undefined,
				openAIServiceTier: parsed.serviceTier === undefined ? undefined : serviceTierSettingToTier(parsed.serviceTier) ?? null,
				deadline: parsed.maxTime === undefined ? undefined : Date.now() + parsed.maxTime * 1000,
			});
		}));
		const session = created.session;
		if (parsed.untrustedProject) {
			// What the folder would have run, for the client's restricted-folder card (paths only).
			const items = await collectUntrustedProjectItems(projectCwd, getAgentDir(), settings);
			setUntrustedProjectReport(session, { items });
		}
		const finalArgs = applyExtensionFlags(session.extensionRunner, args) ?? parsed;
		if (finalArgs.invalidFlagValues.length) throw new Error(finalArgs.invalidFlagValues.join("\n"));
		if (finalArgs.unrecognizedFlags.length) throw new Error(`Unrecognized session flags: ${finalArgs.unrecognizedFlags.join(", ")}`);
		if (finalArgs.messages.length || finalArgs.fileArgs.length) throw new Error("Send prompts and attachments through RPC, not session launch arguments");
		if (parsed.apiKey && session.model) session.modelRegistry.authStorage.keys.setRuntime(session.model.provider, parsed.apiKey);
		if (resumeOnCurrentModel && (selected || parsed.continue) && session.model) {
			sessionManager.appendModelChange(`${session.model.provider}/${session.model.id}`);
		}
		return created;
	} catch (error) {
		if (created) await created.session.dispose();
		else await sessionManager.close();
		throw error;
	}
}
