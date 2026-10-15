const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs');
const spec=require('./frames/registration.json');
test('24 full drawings have measured clear margins and a common scale',()=>{
 assert.equal(spec.frames.length,24);assert.equal(spec.labels.length,24);assert.equal(spec.durations.length,24);
 for(const f of spec.frames){assert.ok(f.ink>1000);assert.ok(f.left>=2&&f.right<=f.cw-3&&f.top>=2&&f.bottom<=f.ch-3);assert.ok(f.anchorX>=f.left&&f.anchorX<=f.right);assert.equal(f.anchorY,f.bottom)}
 assert.equal(spec.height,Math.max(...spec.frames.map(f=>f.bottom-f.top+1)));
});
test('timing retains quiet holds and a faster recoil, with a single frame per instant',()=>{
 assert.ok(spec.durations[0]>=300);assert.ok(spec.durations[11]>=250);assert.ok(spec.durations[12]<spec.durations[8]);assert.ok(spec.durations[23]>=300);
 let t=0;for(const d of spec.durations){assert.ok(Number.isFinite(d)&&d>0);t+=d}assert.equal(t,3050);
});
test('the archived frame preview uses full frames and contains no skeleton/native integration hooks',()=>{
 const html=fs.readFileSync(__dirname+'/studies/rejected-drawn-preview-v2.html','utf8');assert.ok(html.includes('24 кадра · цельные позы'));assert.ok(!html.includes('OniRig'));assert.ok(html.includes('index===23?0:index'));assert.ok(html.includes('prefers-reduced-motion'));
});

test('rejected frontal turn is absent and the final hold reuses the opening drawing',()=>{
 assert.ok(spec.frames.every(f=>!(f.atlas===0&&f.sourceFrame===16)));
 assert.deepEqual(spec.frames.at(-1),spec.frames[0]);
});
