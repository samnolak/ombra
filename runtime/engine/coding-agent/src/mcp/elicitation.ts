/**
 * MCP form elicitation (`elicitation/create`, MCP 2025-11-25 form mode).
 *
 * A server asks the user for a flat form of primitive fields. The request is
 * normalized into {@link MCPElicitationField}s a host can render without
 * interpreting JSON Schema, and the user's answer is validated against those
 * fields before it is returned to the server: a host bug or a tampered answer
 * never reaches the server as an acceptance.
 */
import { isRecord } from "@oh-my-pi/pi-utils";

/** Upper bounds that keep one request renderable as a single form card. */
const MAX_FIELDS = 32;
const MAX_OPTIONS = 100;
const MAX_TEXT_CHARS = 10_000;
const MAX_MESSAGE_CHARS = 4_000;

export type MCPElicitationFormat = "email" | "uri" | "date" | "date-time";

export interface MCPElicitationOption {
	value: string;
	label: string;
}

/** One form field, in request order. Optional keys are present only when the schema sets them. */
export type MCPElicitationField = {
	name: string;
	title: string;
	description?: string;
	required: boolean;
} & (
	| { kind: "text"; format?: MCPElicitationFormat; minLength?: number; maxLength?: number; default?: string }
	| { kind: "number" | "integer"; minimum?: number; maximum?: number; default?: number }
	| { kind: "boolean"; default?: boolean }
	| { kind: "select"; options: MCPElicitationOption[]; default?: string }
	| { kind: "multiselect"; options: MCPElicitationOption[]; minItems?: number; maxItems?: number; default?: string[] }
);

export interface MCPElicitationRequest {
	message: string;
	fields: MCPElicitationField[];
}

export type MCPElicitationValue = string | number | boolean | string[];

export type MCPElicitationResult =
	| { action: "accept"; content: Record<string, MCPElicitationValue> }
	| { action: "decline" | "cancel" };

/** JSON-RPC "invalid params": the transports send `code` back to the server. */
export class MCPElicitationError extends Error {
	readonly code = -32602;
	constructor(message: string) {
		super(message);
		this.name = "MCPElicitationError";
	}
}

const FORMATS: Record<string, MCPElicitationFormat> = { email: "email", uri: "uri", date: "date", "date-time": "date-time" };

function text(value: unknown): string | undefined {
	return typeof value === "string" && value.trim() !== "" ? value : undefined;
}

function count(value: unknown): number | undefined {
	return typeof value === "number" && Number.isInteger(value) && value >= 0 ? value : undefined;
}

function finite(value: unknown): number | undefined {
	return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/** Single-select options: `enum` (+ legacy `enumNames`) or titled `oneOf: [{ const, title }]`. */
function readOptions(schema: Record<string, unknown>, where: string): MCPElicitationOption[] | undefined {
	let options: MCPElicitationOption[] | undefined;
	if (Array.isArray(schema.enum)) {
		const names = Array.isArray(schema.enumNames) ? schema.enumNames : [];
		options = schema.enum.map((value, index) => {
			if (typeof value !== "string") throw new MCPElicitationError(`${where}: enum values must be strings`);
			return { value, label: text(names[index]) ?? value };
		});
	} else if (Array.isArray(schema.oneOf) || Array.isArray(schema.anyOf)) {
		const entries = (schema.oneOf ?? schema.anyOf) as unknown[];
		options = entries.map(entry => {
			if (!isRecord(entry) || typeof entry.const !== "string")
				throw new MCPElicitationError(`${where}: titled options need a string const`);
			return { value: entry.const, label: text(entry.title) ?? entry.const };
		});
	}
	if (!options) return undefined;
	if (options.length === 0 || options.length > MAX_OPTIONS)
		throw new MCPElicitationError(`${where}: between 1 and ${MAX_OPTIONS} options are supported`);
	return options;
}

function readField(name: string, schema: unknown, required: boolean): MCPElicitationField {
	const where = `Field "${name}"`;
	if (!isRecord(schema)) throw new MCPElicitationError(`${where} has no schema`);
	const base = {
		name,
		title: text(schema.title) ?? name,
		...(text(schema.description) ? { description: schema.description as string } : {}),
		required,
	};
	switch (schema.type) {
		case "string": {
			const options = readOptions(schema, where);
			if (options) {
				const fallback = typeof schema.default === "string" && options.some(o => o.value === schema.default);
				return { ...base, kind: "select", options, ...(fallback ? { default: schema.default as string } : {}) };
			}
			const format = typeof schema.format === "string" ? FORMATS[schema.format] : undefined;
			if (schema.format !== undefined && !format) throw new MCPElicitationError(`${where}: unsupported format`);
			const minLength = count(schema.minLength);
			const maxLength = count(schema.maxLength);
			return {
				...base,
				kind: "text",
				...(format ? { format } : {}),
				...(minLength !== undefined ? { minLength } : {}),
				...(maxLength !== undefined ? { maxLength } : {}),
				...(typeof schema.default === "string" ? { default: schema.default } : {}),
			};
		}
		case "number":
		case "integer": {
			const minimum = finite(schema.minimum);
			const maximum = finite(schema.maximum);
			const fallback = finite(schema.default);
			return {
				...base,
				kind: schema.type,
				...(minimum !== undefined ? { minimum } : {}),
				...(maximum !== undefined ? { maximum } : {}),
				...(fallback !== undefined ? { default: fallback } : {}),
			};
		}
		case "boolean":
			return { ...base, kind: "boolean", ...(typeof schema.default === "boolean" ? { default: schema.default } : {}) };
		case "array": {
			const options = isRecord(schema.items) ? readOptions(schema.items, where) : undefined;
			if (!options) throw new MCPElicitationError(`${where}: arrays must list string options`);
			const minItems = count(schema.minItems);
			const maxItems = count(schema.maxItems);
			const fallback = Array.isArray(schema.default) &&
				schema.default.every(value => typeof value === "string" && options.some(o => o.value === value));
			return {
				...base,
				kind: "multiselect",
				options,
				...(minItems !== undefined ? { minItems } : {}),
				...(maxItems !== undefined ? { maxItems } : {}),
				...(fallback ? { default: schema.default as string[] } : {}),
			};
		}
		default:
			throw new MCPElicitationError(`${where}: only string, number, integer, boolean and option fields are supported`);
	}
}

/** Normalizes `elicitation/create` params; throws {@link MCPElicitationError} for anything but a flat form. */
export function parseElicitationRequest(params: unknown): MCPElicitationRequest {
	if (!isRecord(params)) throw new MCPElicitationError("Elicitation params must be an object");
	if (params.mode !== undefined && params.mode !== "form")
		throw new MCPElicitationError("Only form elicitation is supported");
	const message = typeof params.message === "string" ? params.message.trim() : "";
	if (!message) throw new MCPElicitationError("Elicitation needs a message");
	if (message.length > MAX_MESSAGE_CHARS) throw new MCPElicitationError("Elicitation message is too long");
	const schema = params.requestedSchema;
	if (!isRecord(schema) || schema.type !== "object" || !isRecord(schema.properties))
		throw new MCPElicitationError("requestedSchema must be an object schema with properties");
	const required = new Set(Array.isArray(schema.required) ? schema.required.filter(n => typeof n === "string") : []);
	const names = Object.keys(schema.properties);
	if (names.length > MAX_FIELDS) throw new MCPElicitationError(`At most ${MAX_FIELDS} fields are supported`);
	const fields = names.map(name => readField(name, (schema.properties as Record<string, unknown>)[name], required.has(name)));
	return { message, fields };
}

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

function checkFormat(value: string, format: MCPElicitationFormat): boolean {
	switch (format) {
		case "email":
			return EMAIL.test(value);
		case "uri":
			return URL.canParse(value);
		case "date":
			return DATE.test(value) && !Number.isNaN(Date.parse(value));
		case "date-time":
			return !DATE.test(value) && !Number.isNaN(Date.parse(value));
	}
}

function checkValue(field: MCPElicitationField, value: unknown): MCPElicitationValue {
	const bad = (why: string) => new MCPElicitationError(`Field "${field.name}" ${why}`);
	switch (field.kind) {
		case "text": {
			if (typeof value !== "string") throw bad("must be text");
			if (value.length > MAX_TEXT_CHARS) throw bad("is too long");
			if (field.minLength !== undefined && value.length < field.minLength) throw bad("is too short");
			if (field.maxLength !== undefined && value.length > field.maxLength) throw bad("is too long");
			if (field.format && !checkFormat(value, field.format)) throw bad(`must be a valid ${field.format}`);
			return value;
		}
		case "number":
		case "integer": {
			if (typeof value !== "number" || !Number.isFinite(value)) throw bad("must be a number");
			if (field.kind === "integer" && !Number.isInteger(value)) throw bad("must be a whole number");
			if (field.minimum !== undefined && value < field.minimum) throw bad(`must be at least ${field.minimum}`);
			if (field.maximum !== undefined && value > field.maximum) throw bad(`must be at most ${field.maximum}`);
			return value;
		}
		case "boolean":
			if (typeof value !== "boolean") throw bad("must be true or false");
			return value;
		case "select":
			if (typeof value !== "string" || !field.options.some(o => o.value === value)) throw bad("must be one of its options");
			return value;
		case "multiselect": {
			if (!Array.isArray(value) || !value.every(v => typeof v === "string" && field.options.some(o => o.value === v)))
				throw bad("must list its options");
			const unique = [...new Set(value as string[])];
			if (field.minItems !== undefined && unique.length < field.minItems) throw bad(`needs at least ${field.minItems}`);
			if (field.maxItems !== undefined && unique.length > field.maxItems) throw bad(`allows at most ${field.maxItems}`);
			return unique;
		}
	}
}

/**
 * Validates an accepted answer against the request's fields: only known
 * fields, every required one present, each value of its field's type and
 * limits. Returns the content to send; throws {@link MCPElicitationError}.
 */
export function validateElicitationContent(
	fields: readonly MCPElicitationField[],
	content: unknown,
): Record<string, MCPElicitationValue> {
	if (!isRecord(content)) throw new MCPElicitationError("Accepted elicitation needs content");
	const byName = new Map(fields.map(field => [field.name, field]));
	const result: Record<string, MCPElicitationValue> = {};
	for (const [name, value] of Object.entries(content)) {
		const field = byName.get(name);
		if (!field) throw new MCPElicitationError(`Unknown field "${name}"`);
		if (value === undefined || value === null) continue;
		result[name] = checkValue(field, value);
	}
	for (const field of fields) {
		if (field.required && !(field.name in result)) throw new MCPElicitationError(`Field "${field.name}" is required`);
	}
	return result;
}
