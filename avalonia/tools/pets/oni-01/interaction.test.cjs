const {test}=require('node:test'),a=require('node:assert/strict'),{DragBody,spring}=require('./interaction.cjs');
const bounds={left:60,right:640,top:150,floor:350};
const make=()=>new DragBody(300,350,bounds);
test('release and re-grab preserve momentum without position teleport',()=>{const b=make();b.grab(300,300);b.move(480,160);for(let i=0;i<30;i++)b.step(1/120);const x=b.x,v=b.vx,y=b.y,vy=b.vy,ang=b.angle;b.release();a.equal(b.x,x);a.equal(b.y,y);a.equal(b.vx,v);a.equal(b.vy,vy);a.equal(b.angle,ang);b.grab(500,200);a.equal(b.x,x);a.equal(b.vx,v);});
test('left/right reversal crosses velocity smoothly, rather than resetting on input',()=>{const b=make();b.grab(300,300);b.move(570,200);for(let i=0;i<12;i++)b.step(1/120);a.ok(b.vx>0);const v=b.vx;b.move(90,200);a.equal(b.vx,v);for(let i=0;i<30;i++)b.step(1/120);a.ok(b.vx<0);a.ok(Number.isFinite(b.angle));});
test('release falls, lands within bounds and dissipates velocity',()=>{const b=make();b.grab(300,300);b.move(550,100);for(let i=0;i<20;i++)b.step(1/120);b.release();for(let i=0;i<900;i++)b.step(1/120);a.equal(b.y,bounds.floor);a.ok(Math.abs(b.vx)<.001);a.ok(Math.abs(b.angle)<.001);a.ok(b.phase<.001);a.ok(b.impacts>=1);a.ok(b.x>=bounds.left&&b.x<=bounds.right)});
test('spring solution is time subdivision independent for a fixed target',()=>{const [x,v]=spring(0,100,60,15,.1);let xx=0,vv=100;for(let i=0;i<10;i++)[xx,vv]=spring(xx,vv,60,15,.01);a.ok(Math.abs(x-xx)<1e-10);a.ok(Math.abs(v-vv)<1e-10)});
test('30/60/120 Hz produce the same constant-target drag and landing',()=>{const snapshots=[30,60,120].map(fps=>{const b=make();b.grab(300,300);b.move(550,120);for(let i=0;i<fps;i++)b.step(1/fps);b.release();for(let i=0;i<fps*3;i++)b.step(1/fps);return b});for(const b of snapshots){a.ok(Math.abs(b.x-snapshots[0].x)<.1);a.ok(Math.abs(b.angle-snapshots[0].angle)<.001)}});
test('fast upward release stays inside stage instead of disappearing above it',()=>{const b=make();b.air=true;b.vy=-6000;for(let i=0;i<240;i++){b.step(1/120);a.ok(b.y>=bounds.top);a.ok(b.y<=bounds.floor)}a.equal(b.air,false)});
test('question reversal retains displayed phase and velocity at interruption',()=>{let [p,v]=spring(0,0,1,7,.16);const before=p,velocity=v;[p,v]=spring(p,v,0,7,0);a.equal(p,before);a.equal(v,velocity);for(let i=0;i<180;i++)[p,v]=spring(p,v,0,7,1/120);a.ok(Math.abs(p)<.001)});

test('nape acceleration leads the feet in both directions',()=>{for(const direction of [-1,1]){const b=make();b.grab(300,300);for(let i=0;i<18;i++){b.move(300+direction*i*2,220);b.step(1/120)}a.ok(b.angle*direction>0);const feetX=b.x-Math.sin(b.angle)*b.length;a.ok((feetX-b.x)*direction<0)}});
test('suspended angle settles at constant velocity instead of permanently leaning',()=>{const b=make();b.bounds={left:-1e5,right:1e5,top:0,floor:500};b.grab(300,300);for(let i=0;i<120*5;i++){b.move(300+i*1.5,220);b.step(1/120)}a.ok(Math.abs(b.angle)<.003);a.ok(b.vx>100)});
test('spirit follower lags and settles without resetting velocity',()=>{const {SpiritFollower}=require('./interaction.cjs'),p=new SpiritFollower(0,0);p.step(.1,100,30);a.ok(p.x>0&&p.x<100);const v=p.vx;p.step(.001,-100,30);a.ok(p.vx>0);a.ok(p.vx<v);for(let i=0;i<400;i++)p.step(1/120,-100,30);a.ok(Math.abs(p.x+100)<.01)});
test('carrying does not layer unrelated state effects',()=>{const {policy}=require('./effects-player.cjs');a.deepEqual(policy.dragging,['crossed-orbit-spirits']);a.deepEqual(policy.thinking,['pondering-spirits','dots']);a.deepEqual(policy.waiting,['pulsing-spirits','?'])});

test('heavy oni never exceeds the ten-degree tilt envelope during repeated hard reversals',()=>{const b=make();b.grab(300,300);for(let i=0;i<1200;i++){b.move(i%60<30?90:570,220);b.step(1/120);a.ok(Math.abs(b.angle)<=.18)}b.move(b.x,220);for(let i=0;i<240;i++)b.step(1/120);a.ok(Math.abs(b.angle)<.01)});
test('crossed orbits pass both behind and in front with no positional seam',()=>{const {orbit}=require('./interaction.cjs');for(let i=0;i<2;i++){a.ok(orbit(Math.PI/8.8,i,128).depth*orbit(3*Math.PI/8.8,i,128).depth<0);const a0=orbit(0,i,128),a1=orbit(2*Math.PI/4.4,i,128);a.ok(Math.abs(a0.x-a1.x)<1e-8);a.ok(Math.abs(a0.y-a1.y)<1e-8)}a.ok(orbit(0,0,128).y/orbit(0,0,128).x>0);a.ok(orbit(0,1,128).y/orbit(0,1,128).x<0)});

test('question ends on frame 11; rejected head-tilt frame 12 is never sampled',()=>{const {transitionFrame}=require('./interaction.cjs'),q=require('./transitions/manifest.json').question;a.equal(transitionFrame(q,1),10);for(let i=0;i<=100;i++)a.notEqual(transitionFrame(q,i/100),11)});

test('spirits hide through a visible sleep entry and remain bounded in every state',()=>{
 const {spiritPose,policy}=require('./effects-player.cjs');
 for(let i=0;i<2;i++){
  a.ok(spiritPose('sleeping',0,i).alpha>0);a.equal(spiritPose('sleeping',2,i).alpha,0);
  a.ok(Math.abs(spiritPose('sleeping',2,i).x)<Math.abs(spiritPose('sleeping',0,i).x));
  for(const id of Object.keys(policy).filter(id=>id!=='dragging'))for(let n=0;n<600;n++){
   const p=spiritPose(id,n/60,i);for(const v of Object.values(p))a.ok(Number.isFinite(v));
   a.ok(Math.abs(p.x)<.7);a.ok(p.y>=-.9&&p.y<=0);a.ok(p.scale>0&&p.scale<.3);a.ok(p.alpha>=0&&p.alpha<=1);
  }
 }
});
