/**
 * Wire definitions for non-command frames: unsolicited server notifications,
 * the extension UI sub-protocol, and the host tool/URI sub-protocols.
 */
import { absentAs, doc, type WireDefs } from "./dsl";

const JSON_OBJECT = "Record<string, unknown>";
const UI = "'extension_ui_request'";

export const frameDefs = {
	EditorDocumentSnapshot: {
		uri: "string", version: "number.integer", text: "string", languageId: "string",
		baseHash: "string", dirty: "boolean", "conflict?": "boolean",
	},
	EditorApplyEditEvent: {
		type: "'editor_apply_edit'", requestId: "string", uri: "string",
		expectedRevision: "number.integer", expectedHash: "string", text: "string",
	},
	ReadyEvent: doc(
		{
			type: "'ready'",
			"protocolVersion?": "number.integer",
			"supportedProtocolVersions?": "number.integer[]",
			"maxFrameBytes?": "number.integer",
			"maxReassembledFrameBytes?": "number.integer",
		},
		"First frame after startup; transport fields are absent on servers without protocol v2.",
	),
	PromptStatus: "'completed' | 'aborted' | 'error'",
	PromptError: doc(
		{
			message: "string",
			"provider?": "string",
			"model?": "string",
			"httpStatus?": "number.integer",
			retryable: doc("boolean", "Transient: resubmitting later may succeed (omp's own retries are exhausted)."),
		},
		'Failure detail of a `prompt_result` with `status: "error"`.',
	),
	PromptResultEvent: doc(
		{
			type: "'prompt_result'",
			"id?": "string",
			agentInvoked: "boolean",
			status: "PromptStatus",
			"error?": "PromptError",
			sessionSettled: doc(
				"boolean",
				"Nothing will wake the session again; when false a `session_settled` follows once background work drains.",
			),
		},
		"Terminal outcome of one accepted `prompt` / `abort_and_prompt`, keyed by request `id`.",
	),
	SessionSettledEvent: doc(
		{ type: "'session_settled'" },
		"The session went quiet: the last run yielded and no background work can wake it.",
	),
	ExtensionError: { type: "'extension_error'", extensionPath: "string", event: "string", error: "string" },
	AvailableCommandsUpdateEvent: doc(
		{ type: "'available_commands_update'", commands: "AvailableSlashCommand[]" },
		"Slash-command catalog, pushed at startup and whenever command metadata changes.",
	),
	SubagentLifecycleStatus: "'started' | 'completed' | 'failed' | 'aborted'",
	SubagentLifecyclePayload: {
		id: "string",
		agent: "string",
		agentSource: "AgentSource",
		"description?": "string",
		status: "SubagentLifecycleStatus",
		"sessionFile?": "string",
		"parentToolCallId?": "string",
		index: "number.integer",
		"detached?": doc("boolean", "The subagent runs as a detached background job."),
	},
	SubagentProgressPayload: {
		index: "number.integer",
		agent: "string",
		agentSource: "AgentSource",
		task: "string",
		"parentToolCallId?": "string",
		"assignment?": "string",
		progress: doc(JSON_OBJECT, "Raw `AgentProgress` record."),
		"sessionFile?": "string",
		"detached?": "boolean",
	},
	SubagentEventPayload: { id: "string", event: "RpcAgentEvent" },
	SubagentLifecycleEvent: doc(
		{ type: "'subagent_lifecycle'", payload: "SubagentLifecyclePayload" },
		'A subagent started or ended; sent at subscription level "progress" or "events".',
	),
	SubagentProgressEvent: doc(
		{ type: "'subagent_progress'", payload: "SubagentProgressPayload" },
		'Aggregated subagent progress; sent at subscription level "progress" or "events".',
	),
	SubagentEvent: doc(
		{ type: "'subagent_event'", payload: "SubagentEventPayload" },
		'A subagent\'s own session event; sent only at subscription level "events".',
	),
	LivePhase: "'connecting' | 'listening' | 'working' | 'speaking' | 'muted' | 'error'",
	LiveRole: "'user' | 'assistant'",
	LivePhaseEvent: { type: "'live_phase'", phase: "LivePhase" },
	LiveLevelsEvent: doc(
		{ type: "'live_levels'", input: "number", output: "number" },
		"Microphone (`input`) and speaker (`output`) RMS in [0, 1], at most every 100 ms.",
	),
	LiveTranscriptEvent: doc(
		{ type: "'live_transcript'", role: "LiveRole", turn: "number.integer", text: "string", final: "boolean" },
		"Accumulated text of one realtime turn; replaces earlier frames with the same `role` and `turn`.",
	),
	LiveEndEvent: doc(
		{ type: "'live_end'", "error?": "string" },
		"Sent exactly once when a live session ends; `error` carries the failure cause.",
	),
	BtwDeltaEvent: doc(
		{ type: "'btw_delta'", recordId: "string", delta: "string" },
		"Text appended to the running side question's latest answer.",
	),
	BtwRecordEvent: doc(
		{ type: "'btw_record'", record: "BtwHistoryRecord" },
		"Full side-question record on every lifecycle change (started, complete, cancelled, error); the last one per id wins.",
	),
	PlanModeUpdatedEvent: doc(
		{ type: "'plan_mode_updated'", state: "PlanModeState | null", review: "PlanReview | null" },
		"Plan mode changed: entered, exited, a plan proposed for review (with its document), approved or rejected.",
	),
	McpAuthRequiredEvent: doc(
		{ type: "'mcp_auth_required'", serverName: "string", reason: "'signed_out' | 'expired'" },
		"A connector needs the user to sign in (again); nothing was opened. Offer `mcp_login`.",
	),
	SessionLeaseHolder: doc(
		{
			id: doc("string", "The holder process's lease identity (random per process)."),
			kind: doc("string", "`ombra` for the desktop app's engine, `omp` for the terminal and CLI."),
			pid: "number.integer",
			"label?": "string",
			since: doc("string", "When it took the lease (ISO 8601)."),
		},
		"Who holds (or asked for) a session's writer lease, as that process published itself.",
	),
	SessionLeaseLostEvent: doc(
		{
			type: "'session_lease_lost'",
			holder: "SessionLeaseHolder | null",
			interrupted: doc("boolean", "A reply was in progress: it is stopped, not finished."),
		},
		"Another window or terminal asked to write this session (`holder`, null when unknown): the run is aborted and the logical session closes after its last write. Show the chat read-only with an action to take it back.",
	),
	CommandOutputEvent: doc({ type: "'command_output'", text: "string" }, "Output of a builtin slash command."),
	SessionInfoUpdateEvent: doc(
		{ type: "'session_info_update'", "title?": "string", sessionId: "string" },
		"A builtin slash command changed the session title.",
	),
	ConfigUpdateEvent: doc(
		{ type: "'config_update'", "model?": "ModelInfo", "thinkingLevel?": "ThinkingLevel" },
		"A builtin slash command changed the model configuration.",
	),
	RpcFrameErrorEvent: doc(
		{ type: "'rpc_frame_error'", "originalType?": "string", error: "string" },
		"An event could not fit within the transport limits and was dropped.",
	),

	WidgetPlacement: "'aboveEditor' | 'belowEditor'",
	SelectOptionDetail: doc({ "description?": "string" }, "Presentation metadata aligned positionally with `options`."),
	AskOption: { label: "string", "description?": "string", "preview?": "string" },
	AskQuestion: doc(
		{
			id: "string",
			question: "string",
			"header?": "string",
			options: "AskOption[]",
			multi: absentAs("boolean", false),
			"recommended?": doc("number.integer", "Index into `options` of the recommended choice."),
		},
		"One question of an `ask` request; hosts always offer free text besides `options`.",
	),
	ToolApprovalRisk: "'read' | 'write' | 'exec'",
	ToolApprovalScope: "'once' | 'session' | 'always'",
	ToolApprovalRequest: doc(
		{
			tool: "string",
			"toolCallId?": "string",
			risk: doc("ToolApprovalRisk", "Capability tier of this call."),
			"reason?": doc("string", "The tool's own reason for asking."),
			"origin?": doc("'mcp'", "A tool of an MCP server."),
			"server?": doc("string", "With `origin`: the MCP server's name."),
			details: doc("string[]", "The tool's argument summary lines, bounded as in the text prompt."),
			"command?": doc("string", "`bash` only: the complete command; absent when longer than 65536 characters."),
			"cwd?": doc("string", "`bash` only: the folder the call runs in, as given (relative to the session's folder)."),
			"safetyChecks?": doc("string[]", "Provider safety checks only a person may confirm."),
			scopes: doc(
				"ToolApprovalScope[]",
				"Scopes a host may keep an approval for; only `once` with safety checks or when the tool or the user's policy insists on asking every time.",
			),
		},
		"Typed tool approval: render it instead of parsing the select `title`.",
	),
	SelectUiRequest: {
		type: UI,
		id: "string",
		method: "'select'",
		title: "string",
		options: "string[]",
		"optionDetails?": "SelectOptionDetail[]",
		"approval?": doc(
			"ToolApprovalRequest",
			"Set on tool approvals (options Approve/Deny); a `Deny` answer may carry `reason`.",
		),
		"timeout?": "number.integer",
	},
	ConfirmUiRequest: {
		type: UI,
		id: "string",
		method: "'confirm'",
		title: "string",
		message: "string",
		"timeout?": "number.integer",
	},
	InputUiRequest: {
		type: UI,
		id: "string",
		method: "'input'",
		title: "string",
		"placeholder?": "string",
		"timeout?": "number.integer",
	},
	EditorUiRequest: {
		type: UI,
		id: "string",
		method: "'editor'",
		title: "string",
		"prefill?": "string",
		"promptStyle?": "boolean",
	},
	AskUiRequest: doc(
		{ type: UI, id: "string", method: "'ask'", questions: "AskQuestion[]", "timeout?": "number.integer" },
		"Every question of one `ask` tool call; sent only after `set_ask_dialog` enables it.",
	),
	CancelUiRequest: doc(
		{ type: UI, id: "string", method: "'cancel'", targetId: "string" },
		"Close the dialog opened by request `targetId`; a later answer to it is ignored.",
	),
	NotifyUiRequest: {
		type: UI,
		id: "string",
		method: "'notify'",
		message: "string",
		"notifyType?": "NotifyType",
	},
	SetStatusUiRequest: {
		type: UI,
		id: "string",
		method: "'setStatus'",
		statusKey: "string",
		"statusText?": "string",
	},
	SetWidgetUiRequest: {
		type: UI,
		id: "string",
		method: "'setWidget'",
		widgetKey: "string",
		"widgetLines?": "string[]",
		"widgetPlacement?": "WidgetPlacement",
	},
	SetTitleUiRequest: { type: UI, id: "string", method: "'setTitle'", title: "string" },
	SetEditorTextUiRequest: { type: UI, id: "string", method: "'set_editor_text'", text: "string" },
	OpenUrlUiRequest: {
		type: UI,
		id: "string",
		method: "'open_url'",
		url: "string",
		"launchUrl?": doc("string", "Short loopback redirect to `url`; the truncation-safe copy target."),
		"instructions?": "string",
		"mcpServer?": doc("string", "Set when the URL belongs to the host's `mcp_login` for this server."),
	},
	ElicitUiRequest: doc(
		{ type: UI, id: "string", method: "'elicit'", serverName: "string", message: "string", fields: "ElicitationField[]" },
		"An MCP server asks the user for a form; show `serverName` and offer Decline. Answers are validated against `fields`.",
	),
	ExtensionUiRequest: doc(
		"SelectUiRequest | ConfirmUiRequest | InputUiRequest | EditorUiRequest | AskUiRequest | CancelUiRequest | NotifyUiRequest | SetStatusUiRequest | SetWidgetUiRequest | SetTitleUiRequest | SetEditorTextUiRequest | OpenUrlUiRequest | ElicitUiRequest",
		"Extension UI request, discriminated by `method`.",
	),
	AskAnswer: doc(
		{ id: "string", selectedOptions: "string[]", "customInput?": "string" },
		"Answer to one `ask` question: exact option labels, plus optional free text.",
	),
	ValueUiResponse: doc(
		{
			type: "'extension_ui_response'",
			id: "string",
			value: "string",
			"reason?": doc("string", "With `Deny` on an `approval` select: why, for the agent's failed tool result."),
		},
		"Answers a `select`, `input`, or `editor` request.",
	),
	ConfirmUiResponse: doc(
		{ type: "'extension_ui_response'", id: "string", confirmed: "boolean" },
		"Answers a `confirm` request.",
	),
	CancelUiResponse: doc(
		{ type: "'extension_ui_response'", id: "string", cancelled: "true", "timedOut?": "boolean" },
		"Dismisses a dialog; `timedOut` reports the host's own deadline (an `ask` then takes its recommended answers).",
	),
	AnswersUiResponse: doc(
		{ type: "'extension_ui_response'", id: "string", answers: "AskAnswer[]" },
		"Answers an `ask` request: one `AskAnswer` per question, in question order.",
	),
	ElicitUiResponse: doc(
		{
			type: "'extension_ui_response'",
			id: "string",
			action: "'accept' | 'decline'",
			"content?": doc("Record<string, string | number | boolean | string[]>", "Field values by name; required with `accept`."),
		},
		"Answers an `elicit` form; dismissing it sends `CancelUiResponse` instead.",
	),
	ExtensionUiResponse: doc(
		"ValueUiResponse | ConfirmUiResponse | CancelUiResponse | AnswersUiResponse | ElicitUiResponse",
		"Host reply to an extension UI request; variants share `type` and differ by their payload key.",
	),

	HostToolCallRequest: {
		type: "'host_tool_call'",
		id: "string",
		toolCallId: "string",
		toolName: "string",
		arguments: JSON_OBJECT,
	},
	HostToolCancelRequest: { type: "'host_tool_cancel'", id: "string", targetId: "string" },
	HostToolResultPayload: doc(
		{
			content: "UserContent[]",
			"details?": "unknown",
			"isError?": "boolean",
			"useless?": "boolean",
			"providerMetadata?": JSON_OBJECT,
		},
		"Tool output: content blocks plus optional details.",
	),
	HostToolUpdate: doc(
		{ type: "'host_tool_update'", id: "string", partialResult: "HostToolResultPayload" },
		"Streams partial output of a pending host tool call.",
	),
	HostToolResult: doc(
		{ type: "'host_tool_result'", id: "string", result: "HostToolResultPayload", "isError?": "boolean" },
		"Completes a pending host tool call; `isError` surfaces the content as a tool error.",
	),
	HostUriOperation: "'read' | 'write'",
	HostUriRequest: {
		type: "'host_uri_request'",
		id: "string",
		operation: "HostUriOperation",
		url: "string",
		"content?": doc("string", "Present for write operations."),
	},
	HostUriCancelRequest: { type: "'host_uri_cancel'", id: "string", targetId: "string" },
	HostUriResult: doc(
		{
			type: "'host_uri_result'",
			id: "string",
			"content?": doc("string", "Required for a successful read."),
			"contentType?": "'text/markdown' | 'application/json' | 'text/plain'",
			"notes?": "string[]",
			"immutable?": "boolean",
			"isError?": "boolean",
			"error?": "string",
		},
		"Completes a pending host URI request.",
	),
	RpcHostRequest: doc(
		"HostToolCallRequest | HostToolCancelRequest | HostUriRequest | HostUriCancelRequest",
		"Server request for host-owned tools and URI schemes, discriminated by `type`.",
	),
	RpcInbound: doc(
		"ExtensionUiResponse | HostToolUpdate | HostToolResult | HostUriResult",
		"Non-command frame the host sends, discriminated by `type`.",
	),
	RpcResponse: doc(
		{
			type: "'response'",
			"id?": doc("string", "The command's `id`; absent for failures the server could not correlate."),
			command: "string",
			success: "boolean",
			"data?": doc("unknown", "Command result on success; its shape is the command's `result`."),
			"error?": doc("string", "Failure message when `success` is false."),
			"code?": doc("string", "Machine-readable failure reason, when one applies."),
		},
		"Response to a command, correlated by `id`.",
	),
	ToolLoadMode: "'essential' | 'discoverable'",
	HostToolDefinition: {
		name: "string",
		"label?": "string",
		description: "string",
		parameters: JSON_OBJECT,
		"hidden?": "boolean",
		"loadMode?": "ToolLoadMode",
		"readsSkillUris?": "boolean",
	},
	HostUriSchemeDefinition: {
		scheme: "string",
		"description?": "string",
		"writable?": "boolean",
		"immutable?": "boolean",
	},
} satisfies WireDefs;
