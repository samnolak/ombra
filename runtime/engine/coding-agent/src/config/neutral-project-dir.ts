import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

let neutralProjectDir: string | undefined;

/**
 * An empty folder outside every project, created once per process. Running the engine's own
 * discovery against it yields what the user's setup contributes (user extensions, hooks, plugins)
 * without anything an untrusted folder would add.
 */
export function getNeutralProjectDir(): string {
	if (neutralProjectDir === undefined) {
		const dir = fs.mkdtempSync(path.join(os.tmpdir(), "omp-neutral-"));
		process.once("exit", () => fs.rmSync(dir, { recursive: true, force: true }));
		neutralProjectDir = dir;
	}
	return neutralProjectDir;
}
