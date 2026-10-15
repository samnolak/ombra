const {test}=require('node:test');
const assert=require('node:assert/strict');
const {poseAt,jointsAt,DURATION}=require('./rig.js');
const dist=(a,b)=>Math.hypot(a.x-b.x,a.y-b.y);
test('all limbs keep their bone lengths and feet stay planted throughout the action',()=>{
 for(let t=0;t<=DURATION;t+=.007){const j=jointsAt(poseAt(t));
  assert.deepEqual(j.feet,[{x:-18,y:120},{x:19,y:120}]);
  for(const a of j.arms){assert.ok(Math.abs(dist(a.root,a.joint)-15)<1e-6);assert.ok(Math.abs(dist(a.joint,a.end)-15)<1e-6);}
  for(const l of j.legs){assert.ok(Math.abs(dist(l.root,l.joint)-15)<1e-6);assert.ok(Math.abs(dist(l.joint,l.end)-15)<1e-6);}
 }
});
test('gesture is articulated: wrist reaches, recoils and returns while head leads the body',()=>{
 const start=poseAt(0),notice=poseAt(.70),reach=poseAt(1.55),recoil=poseAt(1.90);
 assert.ok(Math.abs(notice.headRot)>.04);assert.ok(Math.abs(notice.hipX)<1);
 assert.ok(reach.handX-start.handX>15);assert.ok(recoil.handX<reach.handX-15);
 assert.ok(recoil.hipX<start.hipX);assert.deepEqual(poseAt(DURATION),start);
});
test('no pose discontinuities and no sample depends on previous playback frames',()=>{
 for(let t=0;t<DURATION;t+=.013){const a=poseAt(t),b=poseAt(t+1e-5);for(const k of Object.keys(a)){assert.ok(Number.isFinite(b[k]));assert.ok(Math.abs(b[k]-a[k])<.01,`${t}: ${k}`);}}
 assert.deepEqual(poseAt(1.3),poseAt(1.3));
 assert.deepEqual(poseAt(-10),poseAt(0));assert.deepEqual(poseAt(100),poseAt(DURATION));
});
const {DATA,Player,render}=require('./rig.js');
test('anatomical depth follows the camera: right arm behind torso, left arm in front',()=>{
 const calls=[],parts=Array.from({length:16},(_,i)=>({x:i,y:0,w:1,h:1}));
 const ctx=new Proxy({}, {get:(_,name)=>name==='drawImage'?((image,id)=>calls.push(id)):(()=>{}),set:()=>true});
 render(ctx,{},parts,poseAt(0,'idle'));
 for(const id of [3,4,5])assert.ok(calls.indexOf(id)<calls.indexOf(1),'right arm behind chest');
 for(const id of [6,7,8])assert.ok(calls.indexOf(id)>calls.indexOf(1),'left arm in front of chest');
 assert.equal(calls.at(-1),0,'head closes the neck seam');
});
test('every loop matches pose and velocity at the seam',()=>{
 for(const [state,c]of Object.entries(DATA.states))if(c.loop){const a=poseAt(0,state),b=poseAt(c.duration-1e-6,state),next=poseAt(1e-6,state);for(const k in a){assert.ok(Math.abs(a[k]-b[k])<1e-5,state+': '+k);assert.ok(Math.abs((next[k]-a[k])/1e-6-(a[k]-b[k])/1e-6)<1e-3,state+': velocity '+k)}}
});
test('all state pairs preserve the visible pose when interrupted, including a second interruption',()=>{
 for(const source of Object.keys(DATA.states))for(const target of Object.keys(DATA.states))for(const time of [.05,.3,1.55]){
  const p=new Player(source);for(let i=0;i<time*60;i++)p.step(1/60);const before={...p.pose};p.setMood(target);assert.deepEqual(p.pose,before);
  const next=p.step(1e-6);for(const k in before)assert.ok(Math.abs(next[k]-before[k])<.01,`${source}->${target} ${k}`);
  p.step(.033);const during={...p.pose};p.setMood('idle');assert.deepEqual(p.pose,during);
  for(let i=0;i<90;i++){const q=p.step(1/60);assert.ok(Object.values(q).every(Number.isFinite));assert.ok(q.lid>=0&&q.lid<=1);}
 }
});
test('drag interrupts sleep, mood changes during drag survive release, inertia settles and click returns',()=>{
 const p=new Player('sleeping');p.beginDrag();p.drag(30,.016);for(let i=0;i<20;i++)p.step(1/60);assert.ok(Math.abs(p.pose.sway)>.0001);p.setMood('working');assert.equal(p.state,'dragging');const before={...p.pose};p.release();assert.deepEqual(p.pose,before);assert.equal(p.state,'working');
 for(let i=0;i<180;i++)p.step(1/60);assert.ok(Math.abs(p.pose.sway)<.0001);p.click();p.setMood('waiting');for(let i=0;i<200;i++)p.step(1/60);assert.equal(p.state,'waiting');
 const paused={...p.pose};p.step(0);assert.deepEqual(p.pose,paused);p.step(120);assert.ok(Object.values(p.pose).every(Number.isFinite));
 p.step(.02,true);assert.deepEqual(p.pose,poseAt(0,'waiting'));
});
