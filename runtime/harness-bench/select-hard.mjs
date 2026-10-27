// Hard polyglot subset: the exercises that were not solved in every run of the given polyglot.mjs output dirs.
//
//   bun select-hard.mjs <results-dir>...        # writes polyglot-hard.json (BENCH_HARD_OUT overrides the path)
//
// <results-dir>: a polyglot.mjs <out-dir>; every runs/<lang>-<name>-<run>/result.json in it counts, whatever the
// BENCH_SUBSET or BENCH_ONLY of the invocation that wrote it. Typical input: the full set (BENCH_SUBSET=all) run
// N times on one or more sides. An exercise is hard when at least one of its runs, on any given dir, failed.
// The output has the shape of polyglot-subset.json, so `BENCH_SUBSET=polyglot-hard.json bun polyglot.mjs …` runs it.
import fs from "node:fs";
import path from "node:path";

const HERE = path.dirname(new URL(import.meta.url).pathname);
const dirs = process.argv.slice(2).map(d => path.resolve(d));
if (!dirs.length) {
	console.error("usage: bun select-hard.mjs <polyglot-out-dir>...");
	process.exit(2);
}
const rows = dirs.flatMap(d => {
	const runs = path.join(d, "runs");
	if (!fs.existsSync(runs)) throw new Error(`${runs} does not exist`);
	return fs.readdirSync(runs).map(r => path.join(runs, r, "result.json")).filter(f => fs.existsSync(f)).map(f => JSON.parse(fs.readFileSync(f, "utf8")));
});
const byExercise = Map.groupBy(rows, r => r.exercise);
const hard = [...byExercise].filter(([, rs]) => rs.some(r => !r.pass)).map(([ex]) => ex).sort();
const pinned = JSON.parse(fs.readFileSync(path.join(HERE, "polyglot-subset.json"), "utf8"));
const out = {
	source: pinned.source,
	commit: pinned.commit,
	selection: `exercises not solved in every run: ${rows.length} runs of ${byExercise.size} exercises in ${dirs.map(d => path.relative(process.cwd(), d)).join(", ")}`,
	exercises: hard,
};
const target = path.resolve(process.env.BENCH_HARD_OUT ?? path.join(HERE, "polyglot-hard.json"));
fs.writeFileSync(target, JSON.stringify(out, null, 2) + "\n");
console.log(`${hard.length} of ${byExercise.size} exercises → ${target}`);
for (const ex of hard) { const rs = byExercise.get(ex); console.log(`${ex}\t${rs.filter(r => r.pass).length}/${rs.length}`); }
