/**
 * `todo.disableForModels`: the todo tool per model. A model whose selector contains one of the
 * entries (case-insensitive, like `edit.modelVariants`) gets no `todo` tool, and with it no todo
 * reminders or eager prelude, which require the tool to be active.
 */
import { combine } from "../config/registry";
import type { Settings } from "../config/settings";
import { cfgTodoDisableForModels, cfgTodoEnabled } from "./settings";

const todoModelGate = combine(
	{ enabled: cfgTodoEnabled, disableForModels: cfgTodoDisableForModels },
	({ enabled, disableForModels }) => ({
		enabled,
		patternsLower: disableForModels.filter(pattern => pattern !== "").map(pattern => pattern.toLowerCase()),
	}),
);

/** Whether the todo tool is available to `model` (a selector such as `openai-codex/gpt-6-luna`). */
export function isTodoEnabledForModel(settings: Settings, model: string | undefined): boolean {
	const { enabled, patternsLower } = todoModelGate.get(settings);
	if (!enabled) return false;
	if (!model || patternsLower.length === 0) return true;
	const modelLower = model.toLowerCase();
	return !patternsLower.some(pattern => modelLower.includes(pattern));
}
