const {test}=require('node:test'),a=require('node:assert/strict');
const {Director,script}=require('./scenario-player.cjs'),spec=require('./frames/approved-v1/manifest.json');
const clips=Object.fromEntries(spec.states.map(s=>[s.id,s.clip]));
const make=()=>new Director(clips),run=(d,s)=>{for(let i=0;i<s*120;i++)d.step(1/120)};
test('question and persistent fault take priority over transient reactions',()=>{const d=make();d.event('waiting');d.event('tool-error');d.event('done');a.equal(d.cue,'waiting');d.event('error');run(d,30);a.equal(d.cue,'error');d.event('idle');a.equal(d.cue,'idle')});
test('temporary failure and success expire, abort never celebrates',()=>{const d=make();d.event('working');d.event('tool-error');a.equal(d.cue,'error');run(d,2.5);a.equal(d.cue,'working');d.event('done');a.equal(d.cue,'joy');d.event('abort');a.equal(d.cue,'idle');a.equal(d.flash,null)});
test('sleep, click, typing and automatic wake preserve important cues',()=>{const d=make();d.step(181);a.equal(d.cue,'sleeping');d.event('click');a.equal(d.cue,'idle');d.event('sleep');d.event('working');a.equal(d.cue,'working');d.event('waiting');d.event('type');a.equal(d.cue,'waiting')});
test('reversing entry preserves displayed phase and its velocity',()=>{const d=make();d.event('working');run(d,.3);const s=d.sample(),v=d.velocity;d.event('waiting');a.equal(d.phase,s.phase);a.equal(d.velocity,v);run(d,5);a.equal(d.state,'waiting');a.equal(d.mode,'loop')});
test('drag from every mood preserves current drawing and restores latest state on landing',()=>{for(const id of ['idle','thinking','working','waiting','sleep','error','done']){const d=make();d.event(id);run(d,1);const before=d.sample();d.event('drag-start');a.equal(d.state,before.state);a.equal(d.phase,before.phase);d.event('waiting');a.equal(d.cue,'dragging');run(d,3);d.event('land');a.equal(d.cue,'waiting');run(d,5);a.equal(d.state,'waiting')}});
test('all scripted scenarios reach valid endpoints without unbounded phase',()=>{const d=make();for(const [event,duration] of script){d.event(event);for(let i=0;i<duration*60;i++){d.step(1/60);a.ok(d.phase>=0&&d.phase<=1);a.ok(Number.isFinite(d.velocity));}}a.equal(d.cue,'idle')});
test('stalled clock expires reactions but does not jump the body transition',()=>{const d=make();d.event('done');d.step(.1);const p=d.phase;d.step(60);a.equal(d.cue,'idle');a.ok(Math.abs(d.phase-p)<.6)});
test('success after working actually reaches the joy loop before it expires',()=>{
 const d=make();d.event('working');run(d,3.1);d.event('done');let shown=0;
 for(let i=0;i<8*120;i++){d.step(1/120);if(d.state==='joy'&&d.mode==='loop')shown+=1/120;}
 a.ok(shown>=1.6,'visible joy seconds: '+shown);a.equal(d.cue,'idle');
});
test('new work cancels a stale success reaction',()=>{const d=make();d.event('done');run(d,.3);d.event('working');a.equal(d.cue,'working')});
test('joy exits through the next neutral torso pose without reversing the sway',()=>{
 const d=make();d.event('done');run(d,1.5);a.equal(d.state,'joy');a.equal(d.mode,'loop');
 const previous=d.loopFrame();d.event('waiting');d.step(.1);a.ok(d.settle>previous);
 run(d,3.5);a.equal(d.state,'waiting');a.equal(d.mode,'loop');
});
