import { afterEach, beforeEach, describe, expect, spyOn, test } from "bun:test";
import * as fs from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import type { Subprocess } from "bun";
import { BiomeClient } from "./biome-client";

let directory: string;
let file: string;
const diskContent = "export const stale = true;\n";
const input = "export const value:number=1\n";
const formatted = "export const value: number = 1;\n";

beforeEach(async () => {
	directory = await fs.mkdtemp(path.join(os.tmpdir(), "omp-biome-overlay-"));
	file = path.join(directory, "example.ts");
	await Bun.write(file, diskContent);
});

afterEach(async () => {
	await fs.rm(directory, { recursive: true, force: true });
});

function client(): BiomeClient {
	return new BiomeClient({ command: "biome", resolvedCommand: "overlay-biome", fileTypes: [".ts"], rootMarkers: [] }, directory);
}

function formatProcess(stdout: string, exitCode: number): Subprocess {
	return {
		stdout: new Blob([stdout]).stream(),
		stderr: new Blob([]).stream(),
		exited: Promise.resolve(exitCode),
	} as Subprocess;
}

describe("Biome editor overlay formatting", () => {
	test("sends the supplied buffer on stdin and leaves stale disk content untouched", async () => {
		const spawn = spyOn(Bun, "spawn").mockReturnValue(formatProcess(formatted, 0));
		try {
			expect(await client().format(file, input)).toBe(formatted);
			expect(spawn).toHaveBeenCalledWith(["overlay-biome", "format", "--stdin-file-path", file], {
				cwd: directory,
				stdin: Buffer.from(input),
				stdout: "pipe",
				stderr: "pipe",
				windowsHide: true,
				signal: undefined,
			});
			expect(await Bun.file(file).text()).toBe(diskContent);
		} finally { spawn.mockRestore(); }
	});

	test("a CLI failure preserves both the supplied buffer and its disk base", async () => {
		const spawn = spyOn(Bun, "spawn").mockReturnValue(formatProcess("partial output", 1));
		try {
			expect(await client().format(file, input)).toBe(input);
			expect(await Bun.file(file).text()).toBe(diskContent);
		} finally { spawn.mockRestore(); }
	});

	test("an empty formatted buffer is not replaced by stale disk text", async () => {
		const spawn = spyOn(Bun, "spawn").mockReturnValue(formatProcess("", 0));
		try {
			expect(await client().format(file, "")).toBe("");
			expect(await Bun.file(file).text()).toBe(diskContent);
		} finally { spawn.mockRestore(); }
	});
});
