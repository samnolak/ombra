// Compares two run.mjs or two polyglot.mjs outputs (baseline vs working tree).
//
//   bun compare.mjs <baseline-out-dir> <after-out-dir>
//
// run.mjs: per fault — success, scripted final answer, outside-workspace changes, unrequested edits, requests,
// input KB, wall time. polyglot.mjs: per exercise and in total — the owner's columns (pass, wall time, code written,
// unrequested edits, attempts, spend). Values print as baseline → after when they differ.
import fs from "node:fs";
import path from "node:path";

const [a, b] = process.argv.slice(2).map(d => JSON.parse(fs.readFileSync(path.join(d, "results.json"), "utf8")));
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
const byFault = rows => Map.groupBy(rows, r => r.fault);
const A = byFault(a), B = byFault(b);
let out = "| fault | success | final answer | outside touched | unrequested edits | requests | input KB | wall s |\n|---|---|---|---|---|---|---|---|\n";
for (const [fault, ra] of A) {
	const rb = B.get(fault);
	if (!rb) continue;
	const count = (rows, k) => `${rows.filter(r => r[k]).length}/${rows.length}`;
	const s = (x, y) => (x === y ? x : `${x} → ${y}`);
	const total = (rows, k) => rows.reduce((n, r) => n + (r[k] ?? 0), 0);
	out += `| ${fault} | ${s(count(ra, "success"), count(rb, "success"))} | ${s(count(ra, "finalSeen"), count(rb, "finalSeen"))} | ${s(count(ra, "outsideTouched"), count(rb, "outsideTouched"))} | ${s(total(ra, "unrequestedEdits"), total(rb, "unrequestedEdits"))} | ${pair(avg(ra, r => r.requests), avg(rb, r => r.requests))} | ${pair(avg(ra, r => r.inputBytes / 1024), avg(rb, r => r.inputBytes / 1024))} | ${pair(avg(ra, r => r.wallMs / 1000), avg(rb, r => r.wallMs / 1000))} |\n`;
}
console.log(out);
