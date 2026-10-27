// Compares two run.mjs or two polyglot.mjs outputs (baseline vs working tree, or one engine under two edit modes).
//
//   bun compare.mjs <baseline-out-dir> <after-out-dir> [by=fault|task]
//
// run.mjs: per fault (default) or per task — success (check OK, untouched files byte-identical, clean stdout),
// check OK alone, scripted final answer, outside-workspace changes, unrequested edits, runs with stray stdout lines,
// edit calls and edit errors, requests, input KB (scripted model), provider input ktok (real model), wall time; the
// header names each side's model and edit mode.
// polyglot.mjs: per exercise and in total — the owner's columns (pass, wall time, code written, unrequested edits,
// attempts, spend). Values print as baseline → after when they differ.
import fs from "node:fs";
import path from "node:path";

const [dirA, dirB, BY = "fault"] = process.argv.slice(2);
const [a, b] = [dirA, dirB].map(d => JSON.parse(fs.readFileSync(path.join(d, "results.json"), "utf8")));
const avg = (rows, f) => rows.reduce((n, r) => n + f(r), 0) / rows.length;
const pair = (x, y, digits = 1) => (x.toFixed(digits) === y.toFixed(digits) ? x.toFixed(digits) : `${x.toFixed(digits)} → ${y.toFixed(digits)}`);
if (a[0]?.exercise) {
	const sum = (rows, k) => rows.reduce((n, r) => n + (r[k] ?? 0), 0);
	const line = (label, ra, rb) => {
		const passes = rows => `${rows.filter(r => r.pass).length}/${rows.length}`;
		const p = (x, y) => (x === y ? x : `${x} → ${y}`);
		return `| ${label} | ${p(passes(ra), passes(rb))} | ${pair(avg(ra, r => r.wallMs / 1000), avg(rb, r => r.wallMs / 1000))} | ${pair(avg(ra, r => r.linesAdded + r.linesRemoved), avg(rb, r => r.linesAdded + r.linesRemoved))} | ${p(String(sum(ra, "unrequestedEdits")), String(sum(rb, "unrequestedEdits")))} | ${pair(avg(ra, r => r.requests), avg(rb, r => r.requests))} | ${pair(avg(ra, r => r.toolErrors), avg(rb, r => r.toolErrors))} | ${pair(avg(ra, r => r.testRuns), avg(rb, r => r.testRuns))} | ${pair(sum(ra, "inputTokens") / 1000, sum(rb, "inputTokens") / 1000, 0)} | ${pair(sum(ra, "costUsd"), sum(rb, "costUsd"), 2)} |\n`;
	};
	let out = "| exercise | pass | wall s | lines changed | unrequested edits | requests | tool errors | test runs | input k tok | cost $ |\n|---|---|---|---|---|---|---|---|---|---|\n";
	const A = Map.groupBy(a, r => r.exercise), B = Map.groupBy(b, r => r.exercise);
	for (const [ex, ra] of A) if (B.has(ex)) out += line(ex, ra, B.get(ex));
	out += line("**total**", a.filter(r => B.has(r.exercise)), b.filter(r => A.has(r.exercise)));
	console.log(out);
	process.exit(0);
}
const side = rows => [...new Set(rows.map(r => `${r.model ?? "scripted"}, edit.mode ${r.editModeSeen ?? r.editMode ?? "default"}`))].join("; ");
const groupBy = rows => Map.groupBy(rows, r => (BY === "task" ? r.task : r.fault));
const A = groupBy(a), B = groupBy(b);
let out = `baseline: ${side(a)}\nafter: ${side(b)}\n\n| ${BY} | success | check OK | final answer | outside touched | unrequested edits | stray stdout | edit calls | edit errors | requests | input KB | provider input ktok | wall s |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|\n`;
for (const [key, ra] of A) {
	const rb = B.get(key);
	if (!rb) continue;
	const count = (rows, f) => `${rows.filter(f).length}/${rows.length}`;
	const s = (x, y) => (x === y ? x : `${x} → ${y}`);
	const total = (rows, k) => rows.reduce((n, r) => n + (r[k] ?? 0), 0);
	// Outputs written before checkOk/strayStdout existed: success then meant the check alone.
	const checkOk = r => r.checkOk ?? r.success;
	const stray = r => (r.strayStdout?.length ?? 0) > 0;
	out += `| ${key} | ${s(count(ra, r => r.success), count(rb, r => r.success))} | ${s(count(ra, checkOk), count(rb, checkOk))} | ${s(count(ra, r => r.finalSeen), count(rb, r => r.finalSeen))} | ${s(count(ra, r => r.outsideTouched), count(rb, r => r.outsideTouched))} | ${s(total(ra, "unrequestedEdits"), total(rb, "unrequestedEdits"))} | ${s(count(ra, stray), count(rb, stray))} | ${pair(avg(ra, r => r.editCalls ?? 0), avg(rb, r => r.editCalls ?? 0))} | ${s(total(ra, "editErrors"), total(rb, "editErrors"))} | ${pair(avg(ra, r => r.requests), avg(rb, r => r.requests))} | ${pair(avg(ra, r => r.inputBytes / 1024), avg(rb, r => r.inputBytes / 1024))} | ${pair(avg(ra, r => (r.providerInputTokens ?? 0) / 1000), avg(rb, r => (r.providerInputTokens ?? 0) / 1000))} | ${pair(avg(ra, r => r.wallMs / 1000), avg(rb, r => r.wallMs / 1000))} |\n`;
}
console.log(out);
