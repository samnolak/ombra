import { describe, expect, test } from "bun:test";
import {
	MCPElicitationError,
	type MCPElicitationField,
	parseElicitationRequest,
	validateElicitationContent,
} from "../../mcp/elicitation";
import { isSafeAuthorizationUrl, redactMcpError } from "./rpc-mcp";

const form = (properties: Record<string, unknown>, required: string[] = []) => ({
	message: "Fill this in",
	requestedSchema: { type: "object", properties, required },
});

describe("MCP form elicitation is normalized for hosts", () => {
	test("every primitive kind, titled and untitled options, multi-select and limits", () => {
		const { message, fields } = parseElicitationRequest(form({
			name: { type: "string", title: "Name", minLength: 2, maxLength: 20, default: "Ada" },
			site: { type: "string", format: "uri", description: "Homepage" },
			count: { type: "number", minimum: 0.5, maximum: 9.5 },
			ok: { type: "boolean" },
			tier: { type: "string", oneOf: [{ const: "free", title: "Free" }, { const: "pro" }], default: "pro" },
			tags: { type: "array", items: { anyOf: [{ const: "a", title: "Alpha" }, { const: "b" }] }, minItems: 1, maxItems: 2, default: ["a"] },
			legacy: { type: "string", enum: ["x", "y"], enumNames: ["Ex"] },
		}, ["name"]));
		expect(message).toBe("Fill this in");
		expect(fields).toEqual([
			{ name: "name", title: "Name", required: true, kind: "text", minLength: 2, maxLength: 20, default: "Ada" },
			{ name: "site", title: "site", description: "Homepage", required: false, kind: "text", format: "uri" },
			{ name: "count", title: "count", required: false, kind: "number", minimum: 0.5, maximum: 9.5 },
			{ name: "ok", title: "ok", required: false, kind: "boolean" },
			{ name: "tier", title: "tier", required: false, kind: "select", options: [{ value: "free", label: "Free" }, { value: "pro", label: "pro" }], default: "pro" },
			{ name: "tags", title: "tags", required: false, kind: "multiselect", options: [{ value: "a", label: "Alpha" }, { value: "b", label: "b" }], minItems: 1, maxItems: 2, default: ["a"] },
			{ name: "legacy", title: "legacy", required: false, kind: "select", options: [{ value: "x", label: "Ex" }, { value: "y", label: "y" }] },
		] satisfies MCPElicitationField[]);
	});

	test("anything but a flat form is refused with invalid-params before the user sees it", () => {
		const refusals: unknown[] = [
			{ mode: "url", message: "Go", url: "https://example.com", elicitationId: "1" },
			{ requestedSchema: { type: "object", properties: {} } },
			form({ nested: { type: "object", properties: {} } }),
			form({ list: { type: "array", items: { type: "string" } } }),
			form({ when: { type: "string", format: "phone" } }),
			form({ pick: { type: "string", enum: [1, 2] } }),
			form(Object.fromEntries(Array.from({ length: 33 }, (_, i) => [`f${i}`, { type: "string" }]))),
			{ message: "x".repeat(4001), requestedSchema: { type: "object", properties: {} } },
		];
		for (const params of refusals) {
			const thrown = (() => { try { parseElicitationRequest(params); } catch (e) { return e; } })();
			expect(thrown).toBeInstanceOf(MCPElicitationError);
			expect((thrown as MCPElicitationError).code).toBe(-32602);
		}
	});

	test("an accepted answer must match its fields; values are passed through or normalized", () => {
		const { fields } = parseElicitationRequest(form({
			email: { type: "string", format: "email", maxLength: 40 },
			n: { type: "integer", minimum: 1, maximum: 5 },
			on: { type: "boolean" },
			env: { type: "string", enum: ["staging", "prod"] },
			tags: { type: "array", items: { enum: ["a", "b", "c"] }, maxItems: 2 },
			day: { type: "string", format: "date" },
		}, ["email", "env"]));
		expect(validateElicitationContent(fields, { email: "a@b.co", env: "prod", n: 3, on: false, tags: ["a", "a", "b"], day: "2026-10-17" }))
			.toEqual({ email: "a@b.co", env: "prod", n: 3, on: false, tags: ["a", "b"], day: "2026-10-17" });
		const bad: unknown[] = [
			{ env: "prod" },
			{ email: "a@b.co", env: "qa" },
			{ email: "not-an-email", env: "prod" },
			{ email: "a@b.co", env: "prod", n: 2.5 },
			{ email: "a@b.co", env: "prod", n: 9 },
			{ email: "a@b.co", env: "prod", on: "yes" },
			{ email: "a@b.co", env: "prod", tags: ["a", "b", "c"] },
			{ email: "a@b.co", env: "prod", day: "17/10/2026" },
			{ email: "a@b.co", env: "prod", extra: 1 },
			"not an object",
		];
		for (const content of bad) expect(() => validateElicitationContent(fields, content)).toThrow(MCPElicitationError);
	});
});

describe("MCP sign-in URL policy", () => {
	test("only https, or plain http to this computer, may reach the browser", () => {
		for (const url of ["https://auth.example.com/authorize?x=1", "http://127.0.0.1:3000/callback", "http://localhost:8080/a", "http://[::1]:9/a"])
			expect(isSafeAuthorizationUrl(url)).toBe(true);
		for (const url of ["http://auth.example.com/authorize", "javascript:alert(1)", "file:///etc/passwd", "https://user:pw@example.com/", "ftp://example.com", "not a url", "http://127.0.0.1.evil.com/"])
			expect(isSafeAuthorizationUrl(url)).toBe(false);
	});

	test("connection errors shown to the host lose URL queries (keys travel there) and are capped", () => {
		expect(redactMcpError("fetch failed: https://x.example.com/mcp?apiKey=s3cret#frag (ECONNREFUSED)"))
			.toBe("fetch failed: https://x.example.com/mcp (ECONNREFUSED)");
		expect(redactMcpError("HTTP 401: no [WWW-Authenticate: Bearer resource_metadata=\"https://a.example/m?t=1\"]"))
			.toBe("HTTP 401: no [WWW-Authenticate: Bearer resource_metadata=\"https://a.example/m\"]");
		expect(redactMcpError("e".repeat(600))).toHaveLength(501);
	});
});
