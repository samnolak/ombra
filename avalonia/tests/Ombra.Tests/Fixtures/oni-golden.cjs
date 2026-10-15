// Reference traces of ONI-01's players for the native port (OniParityTests). Regenerate with Bun or Node from the
// repository root:  bun avalonia/tests/Ombra.Tests/Fixtures/oni-golden.cjs
const path = require('path'), fs = require('fs');
const runtime = path.join(__dirname, '../../../tools/pets/oni-01/runtime');
globalThis.OniInteraction = require(path.join(runtime, 'interaction.cjs'));
const {Director, script, interruptions} = require(path.join(runtime, 'scenario-player.cjs'));
const {spiritPose} = require(path.join(runtime, 'effects-player.cjs'));
const {sample} = require(path.join(runtime, 'states-player.cjs'));
const {DragBody} = globalThis.OniInteraction;
const spec = require(path.join(runtime, 'frames/approved-v1/manifest.json'));
const clips = Object.fromEntries(spec.states.map(s => [s.id, s.clip]));
const r = x => Math.round(x * 1e6) / 1e6;

function director(sequence, dt) {
  const d = new Director(clips), rows = [];
  for (const [event, duration] of sequence) {
    const drag = event === 'drag-left' || event === 'drag-right';
    d.event(drag ? 'drag-start' : event);
    const steps = Math.round(duration / dt);
    for (let i = 0; i < steps; i++) {
      d.step(dt);
      const s = d.sample();
      rows.push([s.state, s.mode, s.frame, r(s.phase), s.cue, r(d.velocity)]);
    }
    if (drag) d.event('land');
  }
  return rows;
}

// Uneven frames and a long stall (a laptop lid), from the middle of an entry
function stalls() {
  const d = new Director(clips), rows = [], dts = [1/144, 1/30, 1/60, .25, 1/90, 7, 1/60];
  const events = ['working', 'done', 'tool-error', 'waiting', 'idle', 'sleep', 'type', 'thinking', 'abort', 'error', 'idle'];
  for (let k = 0; k < events.length; k++) {
    d.event(events[k]);
    for (let i = 0; i < 40; i++) {
      d.step(dts[(i + k) % dts.length]);
      const s = d.sample();
      rows.push([s.state, s.mode, s.frame, r(s.phase), s.cue, r(d.velocity)]);
    }
  }
  return rows;
}

function drag() {
  const b = new DragBody(360, 284, {left: 90, right: 630, top: 155, floor: 284}), rows = [];
  b.length = 128 * .4;
  const path = t => t < .5 ? [360 + 400 * t, 284 - 240 * t] : t < 1 ? [560 - 500 * (t - .5), 164 + 60 * (t - .5)] : null;
  b.grab(360, 284);
  const dts = [1/60, 1/60, 1/120, 1/30, 1/60];
  let t = 0;
  for (let i = 0; i < 200; i++) {
    const dt = dts[i % dts.length];
    t += dt;
    const p = path(t);
    if (p) b.move(p[0], p[1]); else if (b.held) b.release();
    b.step(dt);
    rows.push([r(b.x), r(b.y), r(b.vx), r(b.vy), r(b.angle), r(b.phase), b.held, b.air, b.impacts, r(b.impactSpeed)]);
  }
  return rows;
}

function spirits() {
  const rows = [];
  for (const id of ['idle', 'joy', 'working', 'thinking', 'waiting', 'sleeping', 'error', 'dragging'])
    for (let t = 0; t < 7; t += .37)
      for (const i of [0, 1]) {
        const p = spiritPose(id, t, i);
        rows.push([id, r(t), i, r(p.x), r(p.y), r(p.depth), r(p.scale), r(p.alpha), r(p.tempo), r(p.lean)]);
      }
  return rows;
}

function samples() {
  const rows = [];
  for (const s of spec.states)
    for (let t = -500; t < s.clip.duration * 2.2; t += 37.5) rows.push([s.id, t, sample(s.clip, t).frame, sample(s.clip, t, false).frame]);
  return rows;
}

const out = {
  script: director(script, 1/60),
  interruptions: director(interruptions, 1/60),
  stalls: stalls(),
  drag: drag(),
  spirits: spirits(),
  samples: samples(),
};
fs.writeFileSync(path.join(__dirname, 'oni-golden.json'), JSON.stringify(out));
console.log(Object.entries(out).map(([k, v]) => k + ' ' + v.length).join(', '));
