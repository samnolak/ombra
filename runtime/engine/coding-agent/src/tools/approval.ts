/**
 * Tool approval resolution.
 *
 * Approval policy is declared by each tool. This module only knows how to:
 * - normalize user `tools.approval.<tool>: allow | deny | prompt` overrides,
 * - compare a tool capability tier against the active approval mode,
 * - format the generic approval prompt body.
 */
import { isRecord } from "@oh-my-pi/pi-utils";
import type { AgentTool, ToolApprovalDecision, ToolTier } from "@oh-my-pi/pi-agent-core";
import type { Settings } from "../config/settings";
import type { ExtensionUIContext } from "../extensibility/extensions/types";

import { cfgToolsApproval, cfgToolsApprovalMode } from "./settings";

export type { ToolApproval, ToolApprovalDecision, ToolTier } from "@oh-my-pi/pi-agent-core";

export type ApprovalPolicy = "allow" | "deny" | "prompt";
export type ApprovalMode = "always-ask" | "write" | "yolo";

/** The slice of `AgentToolContext` that approval resolution actually reads. */
export type ApprovalContextSource = {
	autoApprove?: boolean;
	settings?: Settings;
};

export interface ResolvedExecuteTimeApproval {
	approvalMode: ApprovalMode;
	userPolicies: Record<string, unknown>;
}

type ApprovalSubject = Pick<AgentTool, "name" | "approval" | "formatApprovalDetails"> & {
	/**
	 * Previous public name of this tool, when a rename changed how it mints.
	 * MCP tools minted before digits were kept carry their digit-stripped name
	 * here so user `deny`/`prompt` policies written against it still apply
	 * (`allow` is deliberately not inherited — see resolveApproval).
	 */
	readonly legacyName?: string;
	/** The MCP server a tool fronts (MCPTool, forwarded through the tool adapters). */
	readonly mcpServerName?: unknown;
};

const APPROVAL_MODES: ReadonlySet<ApprovalMode> = new Set(["always-ask", "write", "yolo"]);

function isApprovalMode(value: unknown): value is ApprovalMode {
	return typeof value === "string" && APPROVAL_MODES.has(value as ApprovalMode);
}

function asPolicyMap(value: unknown): Record<string, unknown> {
	return value !== null && typeof value === "object" && !Array.isArray(value)
		? (value as Record<string, unknown>)
		: {};
}

/**
 * Resolve approval mode and per-tool user policies from the execute-time
 * `AgentToolContext`.
 *
 * Missing context (or context with no settings and no `--auto-approve`) is
 * fail-closed: `always-ask` with an empty policy map — no user grant. When
 * settings are present, the configured `tools.approvalMode` is used (schema
 * default remains `yolo`). `--auto-approve` still forces `yolo`.
 *
 * Shared by `ExtensionToolWrapper.execute`, `refuseByWritePolicy`,
 * `mcpApprovalPreflight`, and eval prelude host calls so those sites cannot
 * drift. `ExtensionToolWrapper.execute` still inherits the runner's session
 * settings when the caller omits context, so a live session keeps its
 * configured (schema-default `yolo`) grant.
 */
export function resolveApprovalFromContext(context?: ApprovalContextSource | null): ResolvedExecuteTimeApproval {
	if (context?.autoApprove === true) {
		return {
			approvalMode: "yolo",
			userPolicies: context.settings ? asPolicyMap(cfgToolsApproval.get(context.settings)) : {},
		};
	}
	const settings = context?.settings;
	if (!settings) {
		return { approvalMode: "always-ask", userPolicies: {} };
	}
	const configured: unknown = cfgToolsApprovalMode.get(settings);
	return {
		approvalMode: isApprovalMode(configured) ? configured : "yolo",
		userPolicies: asPolicyMap(cfgToolsApproval.get(settings)),
	};
}

export interface ResolvedApproval {
	policy: ApprovalPolicy;
	tier: ToolTier;
	reason?: string;
	override: boolean;
	source?: "tool" | "user" | "mode";
	/** User-policy key that produced `source: "user"` (defaults to the tool name). */
	policyKey?: string;
}

const POLICY_VALUES: ReadonlySet<ApprovalPolicy> = new Set(["allow", "deny", "prompt"]);
const TIER_VALUES: ReadonlySet<ToolTier> = new Set(["read", "write", "exec"]);

/** Ordering of capability tiers, least to most privileged. */
export const TIER_RANK: Readonly<Record<ToolTier, number>> = {
	read: 0,
	write: 1,
	exec: 2,
};

/**
 * Fold the per-target decisions of a multi-target write tool (`edit`, `ast_edit`): the first
 * `policy: "deny"` decision wins with its reason (a read-only URL target); otherwise the highest
 * tier, starting from "read".
 */
export function strictestApproval(decisions: Iterable<ToolApprovalDecision>): ToolApprovalDecision {
	let tier: ToolTier = "read";
	for (const decision of decisions) {
		if (typeof decision !== "string" && decision.policy === "deny") return decision;
		const decisionTier = typeof decision === "string" ? decision : decision.tier;
		if (TIER_RANK[decisionTier] > TIER_RANK[tier]) tier = decisionTier;
	}
	return tier;
}

const APPROVAL_MODE_MAX_TIER: Record<ApprovalMode, ToolTier> = {
	"always-ask": "read",
	write: "write",
	yolo: "exec",
};

const DEFAULT_PROMPT_TRUNCATE_CHARS = 2000;

/** Best-effort conversion of an arbitrary user-supplied value to a policy. */
function normalizePolicy(value: unknown): ApprovalPolicy | undefined {
	if (typeof value !== "string") return undefined;
	const lowered = value.trim().toLowerCase();
	return POLICY_VALUES.has(lowered as ApprovalPolicy) ? (lowered as ApprovalPolicy) : undefined;
}

function isToolTier(value: unknown): value is ToolTier {
	return typeof value === "string" && TIER_VALUES.has(value as ToolTier);
}

function normalizeDecision(value: unknown): Omit<ResolvedApproval, "policy"> & { policy?: ApprovalPolicy } {
	if (isToolTier(value)) {
		return { tier: value, override: false };
	}

	if (value && typeof value === "object" && !Array.isArray(value)) {
		const record = value as Record<string, unknown>;
		const tier = isToolTier(record.tier) ? record.tier : "exec";
		const reason = typeof record.reason === "string" && record.reason.length > 0 ? record.reason : undefined;
		const policy = normalizePolicy(record.policy);
		const policyKey =
			typeof record.policyKey === "string" && record.policyKey.length > 0 ? record.policyKey : undefined;
		return {
			tier,
			override: record.override === true,
			...(policy ? { policy } : {}),
			...(reason ? { reason } : {}),
			...(policyKey ? { policyKey } : {}),
		};
	}

	return { tier: "exec", override: false };
}

function getToolDecision(
	tool: ApprovalSubject,
	args: unknown,
): Omit<ResolvedApproval, "policy"> & { policy?: ApprovalPolicy } {
	const approval = tool.approval;
	const decision: ToolApprovalDecision | undefined = typeof approval === "function" ? approval(args) : approval;
	return normalizeDecision(decision);
}

/**
 * Evaluate a tool's own approval declaration against `args` and return the
 * resulting capability tier, defaulting to `exec` when the tool omits an
 * approval. Unlike reading `tool.approval` directly, this runs function-valued
 * approvals — the write tool's `xd://` gate uses it to take a mounted device's
 * argument-dependent tier instead of falling back to `exec`.
 */
export function resolveToolTier(tool: ApprovalSubject, args: unknown): ToolTier {
	return getToolDecision(tool, args).tier;
}

function modeApprovesTier(mode: ApprovalMode, tier: ToolTier): boolean {
	return TIER_RANK[tier] <= TIER_RANK[APPROVAL_MODE_MAX_TIER[mode]];
}

/**
 * Resolve approval policy for a tool call.
 *
 * Resolution order:
 *  1. Tool `approval(args)` decision, defaulting to tier "exec" when omitted.
 *     A decision may carry a `policyKey` — `tools.approval.<policyKey>` is then
 *     the user override consulted instead of `tools.approval.<tool.name>`, with
 *     the invoking tool's own policy as the fallback when the user set none for
 *     the keyed sub-tool (e.g. an `xd://` device dispatch without a device
 *     policy still honors `tools.approval.write`).
 *  2. User per-tool override, if set and valid.
 *  3. Active mode tier comparison.
 *
 * In yolo mode, override-based tool prompts are ignored; user `tools.approval`
 * settings remain authoritative.
 */
export function resolveApproval(
	tool: ApprovalSubject,
	args: unknown,
	mode: ApprovalMode,
	userConfig: Record<string, unknown> = {},
): ResolvedApproval {
	const decision = getToolDecision(tool, args);
	const policyKey = decision.policyKey ?? tool.name;
	const userPolicy = Object.hasOwn(userConfig, policyKey) ? normalizePolicy(userConfig[policyKey]) : undefined;
	const fallbackPolicy =
		policyKey !== tool.name && userPolicy === undefined && Object.hasOwn(userConfig, tool.name)
			? normalizePolicy(userConfig[tool.name])
			: undefined;
	const effectiveUserPolicy = userPolicy ?? fallbackPolicy;
	const userPolicyKey = userPolicy !== undefined ? policyKey : tool.name;

	// Legacy-name fallback for renamed tools (e.g. MCP mints that gained digits).
	// Fail-closed: only `deny`/`prompt` carry over from the old key, so a
	// forgotten restrictive policy keeps protecting the renamed tool, while a
	// stale `allow` cannot mask a `deny` another user sets under the new name.
	const legacyPolicy =
		effectiveUserPolicy === undefined &&
		typeof tool.legacyName === "string" &&
		tool.legacyName !== tool.name &&
		Object.hasOwn(userConfig, tool.legacyName)
			? normalizePolicy(userConfig[tool.legacyName])
			: undefined;
	const inheritedPolicy = legacyPolicy === "deny" || legacyPolicy === "prompt" ? legacyPolicy : undefined;
	const inheritedPolicyKey = inheritedPolicy !== undefined ? tool.legacyName : undefined;
	const combinedUserPolicy = effectiveUserPolicy ?? inheritedPolicy;
	const combinedUserPolicyKey = effectiveUserPolicy !== undefined ? userPolicyKey : inheritedPolicyKey;

	if (decision.policy === "deny") {
		return {
			policy: "deny",
			tier: decision.tier,
			override: decision.override,
			source: "tool",
			...(decision.policyKey ? { policyKey: decision.policyKey } : {}),
			...(decision.reason ? { reason: decision.reason } : {}),
		};
	}
	if (combinedUserPolicy === "deny") {
		return {
			policy: "deny",
			tier: decision.tier,
			override: decision.override,
			source: "user",
			...(combinedUserPolicyKey ? { policyKey: combinedUserPolicyKey } : {}),
		};
	}

	if (mode === "yolo") {
		if (decision.policy) {
			return {
				policy: decision.policy,
				tier: decision.tier,
				override: false,
				source: "tool",
				...(decision.policyKey ? { policyKey: decision.policyKey } : {}),
				...(decision.reason ? { reason: decision.reason } : {}),
			};
		}
		return {
			policy: combinedUserPolicy ?? "allow",
			tier: decision.tier,
			override: false,
			source: combinedUserPolicy ? "user" : "mode",
			...(combinedUserPolicyKey ? { policyKey: combinedUserPolicyKey } : {}),
		};
	}

	if (decision.override) {
		return {
			policy: decision.policy === "allow" ? "allow" : "prompt",
			tier: decision.tier,
			override: true,
			source: "tool",
			...(decision.policyKey ? { policyKey: decision.policyKey } : {}),
			...(decision.reason ? { reason: decision.reason } : {}),
		};
	}

	if (decision.policy === "allow" || decision.policy === "prompt") {
		return {
			policy: decision.policy,
			tier: decision.tier,
			override: false,
			source: "tool",
			...(decision.policyKey ? { policyKey: decision.policyKey } : {}),
			...(decision.reason ? { reason: decision.reason } : {}),
		};
	}

	if (combinedUserPolicy) {
		return {
			policy: combinedUserPolicy,
			tier: decision.tier,
			override: false,
			source: "user",
			...(combinedUserPolicyKey ? { policyKey: combinedUserPolicyKey } : {}),
		};
	}

	if (modeApprovesTier(mode, decision.tier)) {
		return { policy: "allow", tier: decision.tier, override: false, source: "mode" };
	}

	return {
		policy: "prompt",
		tier: decision.tier,
		override: false,
		source: "mode",
		...(decision.reason ? { reason: decision.reason } : {}),
	};
}

/**
 * Error for a resolved deny. Distinguishes tool-owned policy from user config.
 */
export function denyError(resolved: ResolvedApproval, toolName: string): Error {
	const { source, reason, policyKey } = resolved;
	if (source === "tool") {
		return new Error(`Tool "${toolName}" is blocked by tool policy.${reason ? `\nReason: ${reason}` : ""}`);
	}
	return new Error(
		`Tool "${policyKey ?? toolName}" is blocked by user policy.\n` +
			`To allow: remove "tools.approval.${policyKey ?? toolName}: deny" from config.`,
	);
}

/**
 * Check if a tool call requires user approval.
 *
 * @throws Error if policy is 'deny'
 * @returns Object with required flag and optional reason for the prompt
 */
export function requiresApproval(
	tool: ApprovalSubject,
	args: unknown,
	mode: ApprovalMode,
	userConfig: Record<string, unknown> = {},
): { required: boolean; reason?: string } {
	const resolved = resolveApproval(tool, args, mode, userConfig);
	const { policy, reason } = resolved;

	if (policy === "deny") {
		throw denyError(resolved, tool.name);
	}

	if (policy === "prompt") return { required: true, reason };
	return { required: false };
}

export function truncateForPrompt(value: string, maxChars = DEFAULT_PROMPT_TRUNCATE_CHARS): string {
	if (value.length <= maxChars) return value;
	const omitted = value.length - maxChars;
	return `${value.slice(0, maxChars)}[…${omitted}ch elided…]`;
}

/**
 * Format the approval prompt body shown to the user.
 */
export function formatApprovalPrompt(tool: ApprovalSubject, args: unknown, reason?: string): string {
	const lines = [`Allow tool: ${tool.name}`];

	const server = mcpServerOf(tool);
	if (server !== undefined) {
		lines.push(server ? `Origin: MCP server tool (${server})` : "Origin: MCP server tool");
	}

	if (reason) {
		lines.push(`Reason: ${reason}`);
	}

	lines.push(...approvalDetailLines(tool, args));
	return lines.join("\n");
}

/** The MCP server behind `tool` ("" when only its name says MCP); undefined for every other tool. */
function mcpServerOf(tool: ApprovalSubject): string | undefined {
	if (typeof tool.mcpServerName === "string" && tool.mcpServerName) return tool.mcpServerName;
	return tool.name.startsWith("mcp__") && tool.approval === undefined ? "" : undefined;
}

/** How long a host may let an approval answer stand: this call, this session, or a saved pattern. */
export type ToolApprovalScope = "once" | "session" | "always";

/**
 * The typed approval request hosts render natively instead of parsing
 * {@link formatApprovalPrompt}. Sent as `extension_ui_request.approval` with
 * the unchanged Approve/Deny select, so hosts that ignore it keep working.
 */
export interface ToolApprovalRequest {
	tool: string;
	toolCallId?: string;
	/** Capability tier of this call: read, write or exec. */
	risk: ToolTier;
	/** The tool's own reason for asking. */
	reason?: string;
	/** A tool of an MCP server (`server` names it when known). */
	origin?: "mcp";
	server?: string;
	/** The tool's argument summary lines (bounded, as in the text prompt). */
	details: string[];
	/** `bash` only: the complete command, absent when longer than {@link MAX_APPROVAL_COMMAND_CHARS}. */
	command?: string;
	/** `bash` only: the folder the call names to run in (as given; relative to the session's folder). */
	cwd?: string;
	/** Provider safety checks only a person may confirm. */
	safetyChecks?: string[];
	/**
	 * Scopes the host may apply to an approval: only `once` while safety checks are pending and when the tool
	 * insists on a person (an override prompt: a critical command, a user pattern with `approval: prompt`).
	 */
	scopes: ToolApprovalScope[];
}

/** The user's answer; a deny may carry the reason the agent reads in the failed tool result. */
export interface ToolApprovalOutcome {
	approved: boolean;
	reason?: string;
}

export const MAX_APPROVAL_COMMAND_CHARS = 65_536;
/** Longest denial reason forwarded to the agent. */
export const MAX_APPROVAL_DENY_REASON_CHARS = 4_000;

function approvalDetailLines(tool: ApprovalSubject, args: unknown): string[] {
	const details = tool.formatApprovalDetails?.(args);
	if (typeof details === "string") return details.length > 0 ? [details] : [];
	return Array.isArray(details) ? details.filter(detail => detail.length > 0) : [];
}

/** Build the typed request for one prompted call (see {@link ToolApprovalRequest}). */
export function buildToolApprovalRequest(
	tool: ApprovalSubject,
	args: unknown,
	options: { risk: ToolTier; reason?: string; toolCallId?: string; safetyChecks?: readonly string[]; override?: boolean },
): ToolApprovalRequest {
	const command = tool.name === "bash" && isRecord(args) ? args.command : undefined;
	const cwd = tool.name === "bash" && isRecord(args) && typeof args.cwd === "string" && args.cwd.trim() ? args.cwd : undefined;
	const safetyChecks = options.safetyChecks?.length ? [...options.safetyChecks] : undefined;
	const server = mcpServerOf(tool);
	return {
		tool: tool.name,
		...(options.toolCallId ? { toolCallId: options.toolCallId } : {}),
		risk: options.risk,
		...(options.reason ? { reason: options.reason } : {}),
		...(server !== undefined ? { origin: "mcp" as const, ...(server ? { server } : {}) } : {}),
		details: approvalDetailLines(tool, args),
		...(typeof command === "string" && command.length <= MAX_APPROVAL_COMMAND_CHARS ? { command } : {}),
		...(cwd ? { cwd } : {}),
		...(safetyChecks ? { safetyChecks } : {}),
		scopes: safetyChecks || options.override ? ["once"] : ["once", "session", "always"],
	};
}

/**
 * Ask for approval: the text prompt every UI shows, plus the typed request and
 * a channel for the denial reason that hosts with structured approval use.
 */
export async function promptToolApproval(
	ui: Pick<ExtensionUIContext, "select">,
	prompt: string,
	request: ToolApprovalRequest,
): Promise<ToolApprovalOutcome> {
	let denyReason: string | undefined;
	const choice = await ui.select(prompt, ["Approve", "Deny"], {
		approval: request,
		onApprovalDenied: reason => {
			denyReason = reason.trim().slice(0, MAX_APPROVAL_DENY_REASON_CHARS) || undefined;
		},
	});
	if (choice === "Approve") return { approved: true };
	return denyReason ? { approved: false, reason: denyReason } : { approved: false };
}

/**
 * The failed tool result of a denied call: the user's reason, when given, is what the agent reads next. It is a
 * JSON string after a fixed label, so a reason spanning lines cannot pose as further tool output.
 */
export function deniedByUserMessage(subject: string, reason?: string): string {
	return reason ? `${subject}\nThe user said (approval prompt): ${JSON.stringify(reason)}` : subject;
}