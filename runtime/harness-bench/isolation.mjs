// User isolation for engine sides: the engine reads its credentials from the user's agent dir (PI_CODING_AGENT_DIR,
// default ~/.omp/agent), and with it everything else that dir holds. This writes a `--config` overlay that keeps the
// credentials and the harness layer (OMBRA_HARNESS_DIR and its extensions) but drops the user's own items, so an engine
// side sees what a fresh install with the harness sees, as Codex sides get a fresh CODEX_HOME:
// - `disabledProviders: [native]`: the native discovery provider is the one that reads the agent dir (mcp.json,
//   extensions/, rules/, skills/, hooks/, tools/, prompts/, commands/, AGENTS.md, SYSTEM.md) and a project `.omp/`
//   (bench work dirs have none). Harness items come from the `ombra-harness` provider and stay.
// - `disabledExtensions: [mcp:<name>, …]`: every server named in the agent dir's mcp.json/.mcp.json, so the same server
//   stays off if another source declares it.
// - `task.disabledAgents: [<name>, …]`: the agents in <agent-dir>/agents/*.md (task discovery reads that dir directly,
//   not through a provider). A name the harness also defines is kept (disabling it would drop the harness agent too).
// Settings in the user's config.yml still apply (they are below the overlay). Only server and agent names are read;
// nothing from these files is printed or copied. Array settings replace lower layers, so the lists also take the
// values the harness config and the other overlays set.
import fs from "node:fs";
import path from "node:path";

const readYaml = file => (fs.existsSync(file) ? (Bun.YAML.parse(fs.readFileSync(file, "utf8")) ?? {}) : {});
const listed = (settings, key) => {
	const value = key.split(".").reduce((node, part) => (node && typeof node === "object" ? node[part] : undefined), settings);
	return Array.isArray(value) ? value : [];
};

function mcpServerNames(agentDir) {
	const names = [];
	for (const file of ["mcp.json", ".mcp.json"]) {
		const full = path.join(agentDir, file);
		if (!fs.existsSync(full)) continue;
		const doc = JSON.parse(fs.readFileSync(full, "utf8"));
		names.push(...Object.keys(doc.mcpServers ?? {}));
	}
	return names;
}

function agentNames(dir) {
	if (!fs.existsSync(dir)) return [];
	return fs.readdirSync(dir).filter(f => f.endsWith(".md")).flatMap(f => {
		const front = /^---\r?\n([\s\S]*?)\r?\n---/.exec(fs.readFileSync(path.join(dir, f), "utf8"));
		const name = front && Bun.YAML.parse(front[1])?.name;
		return typeof name === "string" ? [name] : [];
	});
}

/**
 * Writes the overlay to `outFile` and returns the `--config` arguments for it; put them after every other `--config`.
 * `harnessDir`: OMBRA_HARNESS_DIR of the side, or undefined. `configs`: the side's other overlay files.
 */
export function userIsolationArgs({ agentDir, harnessDir, configs = [], outFile }) {
	const lower = [...(harnessDir ? [path.join(harnessDir, "agent/config.yml")] : []), ...configs].map(readYaml);
	const union = (key, own) => [...new Set([...lower.flatMap(s => listed(s, key)), ...own])];
	const harnessAgents = new Set(harnessDir ? agentNames(path.join(harnessDir, "agent/agents")) : []);
	const overlay = {
		disabledProviders: union("disabledProviders", ["native"]),
		disabledExtensions: union("disabledExtensions", mcpServerNames(agentDir).map(name => `mcp:${name}`)),
		task: { disabledAgents: union("task.disabledAgents", agentNames(path.join(agentDir, "agents")).filter(n => !harnessAgents.has(n))) },
	};
	fs.mkdirSync(path.dirname(outFile), { recursive: true });
	fs.writeFileSync(outFile, `${JSON.stringify(overlay, null, 2)}\n`);
	return ["--config", outFile];
}
