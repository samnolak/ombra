const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),crypto=require('node:crypto');
const {timeline,sample}=require('./states-player.cjs'),spec=require('./frames/approved-v1/manifest.json');
test('all eight states use intact files derived from the approved appearance',()=>{
 assert.deepEqual(spec.states.map(s=>s.id),['idle','joy','working','thinking','waiting','sleeping','error','dragging']);
 assert.equal(spec.integrationAllowed,false);assert.equal(spec.animationApproved,false);
 for(const s of spec.states){assert.match(s.source,/^(frames\/approved-v1|transitions)\//);const hash=crypto.createHash('sha256').update(fs.readFileSync(__dirname+'/'+s.source)).digest('hex');assert.equal(hash,s.sha256);assert.ok(s.accepted.length>=6);for(const i of s.accepted){const f=s.frames[i];assert.equal(f.clipped,false);assert.ok(f.ink>1000);assert.ok(f.anchorX>=f.left&&f.anchorX<=f.right)}assert.ok(s.scale>0)}
});
test('loop boundaries advance to a neighbouring drawing, never jump between distant poses',()=>{
 for(const s of spec.states){const keys=s.clip.keys;if(s.id!=='joy')for(let i=0;i<keys.length;i++){const next=keys[(i+1)%keys.length];assert.equal(Math.abs(s.accepted.indexOf(next.frame)-s.accepted.indexOf(keys[i].frame)),1)}
  else {assert.equal(keys.length,64);assert.equal(s.clip.fullCycle,true);for(let i=0;i<keys.length;i++)assert.equal(keys[(i+1)%keys.length].frame,(keys[i].frame+1)%64);}
  assert.equal(sample(s.clip,0).frame,sample(s.clip,s.clip.duration).frame);assert.equal(sample(s.clip,-1).frame,sample(s.clip,s.clip.duration-1).frame)}
});
test('sampling respects each hold exactly, including stalled tabs and multiple loops',()=>{
 for(const s of spec.states){for(const key of s.clip.keys){assert.equal(sample(s.clip,key.start).frame,key.frame);assert.equal(sample(s.clip,key.start+key.duration-.001).frame,key.frame)}assert.equal(sample(s.clip,5*s.clip.duration+17).frame,sample(s.clip,17).frame);assert.equal(sample(s.clip,1e8,false).frame,s.clip.keys.at(-1).frame)}
 assert.throws(()=>timeline([0],100,100,100));assert.throws(()=>timeline([0,1],NaN,100,100));
});
test('current viewer has eight whole-frame clips, reduced-motion handling and no rejected rig',()=>{
 const html=fs.readFileSync(__dirname+'/preview.html','utf8');assert.ok(html.includes('восемь анимационных состояний'));assert.ok(html.includes('prefers-reduced-motion'));assert.ok(html.includes('visibilitychange'));assert.ok(!html.includes('OniRig'));assert.ok(!html.includes('curiosity-v1.png'));assert.ok(!html.includes('index===23?0:index'));
});
test('preview uses external assets and reuses shared state/transition images',()=>{
 const html=fs.readFileSync(__dirname+'/preview.html','utf8');
 assert.ok(Buffer.byteLength(html)<300000,'preview must not embed large atlases');
 assert.ok(!html.includes('data:image/'),'images are served once from source paths');
 assert.ok(html.includes('imageCache=new Map()'));
 assert.ok(html.includes('transitionImages[id]=loadImage(assets[id].image)'));
 for(const s of spec.states)assert.ok(html.includes('/'+s.source));
});
