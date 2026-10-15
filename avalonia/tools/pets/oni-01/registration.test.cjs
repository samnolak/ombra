const {test}=require('node:test'),a=require('node:assert/strict');
const spec=require('./frames/approved-v1/manifest.json'),assets=require('./transitions/manifest.json');
test('all cycles and entries use one head scale, not independent full-silhouette fitting',()=>{
 a.ok(spec.registration?.headWidth>0,'missing shared head reference');
 const ref=spec.registration.headWidth;
 for(const state of spec.states){
  a.ok(Math.abs(state.registration.headWidth*state.scale-ref)<1e-6,state.id);
  if(state.entry){const entry=assets[state.entry];a.ok(Math.abs(entry.registration.headWidth/entry.referenceHeight-ref)<1e-6,state.entry);}
 }
});
test('raised hands do not enlarge the measured helmet during error entry',()=>{
 const entry=assets.errorEntry;
 const first=entry.frames[0].headWidth,last=entry.frames.at(-1).headWidth;
 a.ok(Math.abs(last/first-1)<.1,`helmet measurement changed ${first} → ${last}; hands must not define scale`);
});
test('non-joy state loops start on the exact source drawing at the end of their entry',()=>{
 for(const state of spec.states.filter(s=>s.entry&&s.id!=='joy')){
  const entry=assets[state.entry],end=(entry.sequence||entry.frames.map((_,i)=>i)).at(-1),first=state.frames[state.clip.keys[0].frame],last=entry.frames[end];
  a.equal(state.source,entry.source,state.id+' must share the endpoint artwork');
  a.deepEqual([first.sx,first.sy,first.cw,first.ch,first.anchorX,first.anchorY],[last.sx,last.sy,last.cw,last.ch,last.ax,last.ay],state.id);
  a.ok(Math.abs(state.scale-1/entry.referenceHeight)<1e-9,state.id+' endpoint scale');
 }
});
