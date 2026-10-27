import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import {
	SkillDescriptionCatalog,
	type SkillDescriptionCompressor,
	SkillDescriptionStore,
} from "./coding-agent/src/extensibility/skill-descriptions";
import { loadSkillsFromDir } from "./coding-agent/src/extensibility/skills";

// A skill description already within the routing-hint bounds (≤160 chars, ≤12 words) is shown whole and never
// sent to the smol model; on a single local model that call would otherwise go to the only model there is.
describe("skill description compression", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-skill-descriptions");
	const short = "Use for any bug, test failure or unexpected behavior, before proposing fixes";
	const long =
		"Use when encountering any bug, test failure, or unexpected behavior, before proposing fixes, and keep a written log of every hypothesis you rule out along the way.";
	beforeAll(async () => {
		for (const [name, description] of [
			["short-skill", short],
			["long-skill", long],
		]) {
			await mkdir(path.join(testRoot, "skills", name), { recursive: true });
			await writeFile(
				path.join(testRoot, "skills", name, "SKILL.md"),
				`---\nname: ${name}\ndescription: ${description}\n---\n\n# ${name}\n`,
			);
		}
	});
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	test("a fitting description renders verbatim without a model call; a long one is still compressed", async () => {
		const { skills } = await loadSkillsFromDir({ dir: path.join(testRoot, "skills"), source: "custom" });
		const sent: string[] = [];
		const compress: SkillDescriptionCompressor = {
			modelKey: () => "local/only-model",
			async compress(name) {
				sent.push(name);
				return "Bugs and failing tests, before fixing";
			},
		};
		const store = SkillDescriptionStore.open(path.join(testRoot, "descriptions.db"));
		try {
			const catalog = new SkillDescriptionCatalog({ store, compress });
			const rendered = Object.fromEntries(catalog.render(skills).map(skill => [skill.name, skill.description]));
			await catalog.waitForPending();
			expect(rendered["short-skill"]).toBe(short);
			expect(rendered["long-skill"]).not.toBe(long);
			expect(sent).toEqual(["long-skill"]);

			const next = new SkillDescriptionCatalog({ store, compress });
			const snapshot = Object.fromEntries(next.snapshot(skills).map(skill => [skill.name, skill.description]));
			expect(snapshot["short-skill"]).toBe(short);
			expect(Object.fromEntries(next.render(skills).map(skill => [skill.name, skill.description]))).toEqual({
				"short-skill": short,
				"long-skill": "Bugs and failing tests, before fixing",
			});
		} finally {
			await store.close();
		}
	});
});
