(function(root){
 'use strict';
 const DATA=typeof module!=='undefined'?require('./motion.json'):root.OniMotionData;
 const DURATION=DATA.states.curiosity.duration;
 function channel(keys,t){if(t<=keys[0][0])return keys[0][1];for(let i=1;i<keys.length;i++)if(t<=keys[i][0]){const [a,v]=keys[i-1],[b,w]=keys[i];const u=(t-a)/(b-a),e=u*u*u*(u*(u*6-15)+10);return v+(w-v)*e;}return keys.at(-1)[1];}
 function poseAt(t,state='curiosity'){const clip=DATA.states[state];if(!clip)throw Error('Unknown state: '+state);t=Math.max(0,clip.loop?t%clip.duration:Math.min(clip.duration,t));return Object.fromEntries(Object.entries(DATA.neutral).map(([k,v])=>[k,clip.tracks[k]?channel(clip.tracks[k],t):v]));}
 function rotate(x,y,a){return{x:x*Math.cos(a)-y*Math.sin(a),y:x*Math.sin(a)+y*Math.cos(a)}}
 function solve(root,target,a,b,bend){let dx=target.x-root.x,dy=target.y-root.y,d=Math.hypot(dx,dy);d=Math.max(.01,Math.min(a+b-.001,d));const angle=Math.atan2(dy,dx),u={x:Math.cos(angle),y:Math.sin(angle)};const end={x:root.x+u.x*d,y:root.y+u.y*d};const q=(a*a-b*b+d*d)/(2*d),h=Math.sqrt(Math.max(0,a*a-q*q));return{root,joint:{x:root.x+u.x*q-u.y*h*bend,y:root.y+u.y*q+u.x*h*bend},end};}
 function jointsAt(p){const body=(x,y)=>{const r=rotate(x,y,p.bodyRot);return{x:p.hipX+r.x,y:p.hipY+r.y}};const feet=[{x:-18-p.tuck*.35,y:120-p.tuck},{x:19+p.tuck*.35,y:120-p.tuck}];return{
  pelvis:{x:p.hipX,y:p.hipY},body,
  head:body(p.headX,-23+p.headY),
  feet,legs:feet.map((f,i)=>solve(body(i?10:-10,0),f,15,15,i?-1:1)),
  arms:[solve(body(-19,-19),{x:p.farX,y:p.farY},15,15,1),solve(body(12,-21),{x:p.handX,y:p.handY},15,15,1)]
 };}
 function render(ctx,image,parts,p,{skeleton=false,silhouette=false}={}){
  const j=jointsAt(p);
  ctx.save();ctx.translate(0,-p.lift);ctx.translate(0,68);ctx.rotate(p.sway);ctx.translate(0,-68);
  function sprite(id,x,y,w,h,a=0,ax=.5,ay=.5,region=null){const r=parts[id];let sx=r.x,sy=r.y,sw=r.w,sh=r.h;if(region){sx+=r.w*region[0];sy+=r.h*region[1];sw*=region[2];sh*=region[3];}ctx.save();ctx.translate(x,y);ctx.rotate(a);ctx.drawImage(image,sx,sy,sw,sh,-w*ax,-h*ay,w,h);ctx.restore();}
  function limb(id,a,b,w){const angle=Math.atan2(b.y-a.y,b.x-a.x)-Math.PI/2;const len=Math.hypot(b.x-a.x,b.y-a.y);sprite(id,a.x,a.y,w,len+6,angle,.5,.12);}
  function leg(index){const l=j.legs[index],thigh=index?9:11,shoe=index?10:12;
   limb(thigh,l.root,l.joint,index?18:20);
   const angle=Math.atan2(l.end.y-l.joint.y,l.end.x-l.joint.x)-Math.PI/2;
   sprite(shoe,l.joint.x,l.joint.y,index?18:20,19,angle,.5,.10,[0,0,1,.62]);
   sprite(shoe,l.end.x,l.end.y,index?25:28,17,0,.5,.55,[0,.48,1,.52]);
  }
  function arm(index){const a=j.arms[index];limb(index?3:6,a.root,a.joint,index?14:16);limb(index?4:7,a.joint,a.end,index?13:15);sprite(index?5:8,a.end.x,a.end.y,index?12:14,index?13:12,index?p.handRot:.1,.5,.14);}
  // Camera sees the left side of the helmet: screen-right limbs are FAR.
  // Depth is anatomical, independent of the misleading original atlas labels.
  arm(1);leg(1);leg(0);
  sprite(2,j.pelvis.x,j.pelvis.y,36,25,p.bodyRot,.5,.45);
  const chest=j.body(0,-16);sprite(1,chest.x,chest.y,40,34,p.bodyRot,.5,.5);
  if(p.prop>.001){ctx.save();ctx.globalAlpha=p.prop;ctx.fillStyle='#151820';ctx.strokeStyle='#b73c50';ctx.lineWidth=.7;ctx.beginPath();ctx.moveTo(-15,89);ctx.lineTo(22,89);ctx.lineTo(28,110);ctx.lineTo(-9,110);ctx.closePath();ctx.fill();ctx.stroke();ctx.fillStyle='#ff3956';ctx.fillRect(3,96,4,5);ctx.fillStyle='#30313d';ctx.fillRect(-10,110,42,2.4);ctx.restore();}
  arm(0);
  sprite(0,j.head.x,j.head.y,76,66,p.bodyRot+p.headRot,.5,.91);
  // Only the luminous eye area expands; the helmet never swaps or crossfades.
  if(p.eyeOpen>0){ctx.save();ctx.translate(j.head.x,j.head.y);ctx.rotate(p.bodyRot+p.headRot);ctx.globalAlpha=p.eyeOpen;ctx.fillStyle='#fffbee';ctx.shadowColor='#ff3856';ctx.shadowBlur=1.5;for(const e of [[.53,.704,.071,.046],[.807,.712,.045,.044]]){ctx.beginPath();ctx.ellipse((e[0]-.5)*76,(e[1]-.91)*66,e[2]*76,e[3]*66,-.08,0,Math.PI*2);ctx.fill();}ctx.restore();}
  if(p.lid>0){ctx.save();ctx.translate(j.head.x,j.head.y);ctx.rotate(p.bodyRot+p.headRot);for(const e of [[.53,.704,.074,.047],[.807,.712,.048,.046]]){const x=(e[0]-.5)*76,y=(e[1]-.91)*66,rx=e[2]*76,ry=e[3]*66;ctx.save();ctx.beginPath();ctx.ellipse(x,y,rx,ry,-.08,0,Math.PI*2);ctx.clip();ctx.fillStyle='#160f18';ctx.fillRect(x-rx-1,y-ry-1,rx*2+2,(ry*2+2)*Math.min(1,p.lid));ctx.restore();}ctx.restore();}
  if(silhouette){ctx.save();ctx.globalCompositeOperation='source-in';ctx.fillStyle='#08070b';ctx.fillRect(-128,-64,256,256);ctx.restore();}
  if(skeleton){ctx.save();ctx.lineWidth=.65;ctx.strokeStyle='#37dfd1';ctx.fillStyle='#37dfd1';for(const chain of [...j.arms,...j.legs]){ctx.beginPath();ctx.moveTo(chain.root.x,chain.root.y);ctx.lineTo(chain.joint.x,chain.joint.y);ctx.lineTo(chain.end.x,chain.end.y);ctx.stroke();for(const a of [chain.root,chain.joint,chain.end]){ctx.beginPath();ctx.arc(a.x,a.y,1.1,0,Math.PI*2);ctx.fill();}}ctx.restore();}
  ctx.restore();
 }

 class Player {
  constructor(state='idle'){this.state=state;this.desired=state;this.time=0;this.elapsed=1;this.duration=.4;this.offset=null;this.velocity=null;this.clicking=false;this.dragging=false;this.dragTarget=0;this.dragAngle=0;this.dragSpeed=0;this.pose=poseAt(0,state);this.previous={...this.pose};this.dt=1/60;}
  change(state,duration){if(!DATA.states[state])throw Error('Unknown state');const before=this.pose,target=poseAt(0,state);this.offset={};this.velocity={};for(const k in before){this.offset[k]=before[k]-target[k]-(k==='sway'?this.dragAngle:0);this.velocity[k]=Math.max(-100,Math.min(100,(before[k]-this.previous[k])/this.dt-(k==='sway'?this.dragSpeed:0)));}this.state=state;this.time=0;this.elapsed=0;this.duration=duration??DATA.states[state].entry??.42;}
  setMood(state){this.desired=state;if(!this.dragging&&!this.clicking)this.change(state);}
  click(){if(this.dragging)return;this.clicking=true;this.change('done',this.state==='sleeping'?.65:.18);}
  beginDrag(){this.dragging=true;this.clicking=false;this.change('dragging',.28);}
  drag(dx,dt){if(this.dragging)this.dragTarget=Math.max(-.18,Math.min(.18,-dx/Math.max(.01,dt)*.00025));}
  release(){if(!this.dragging)return;this.dragging=false;this.dragTarget=0;this.change(this.desired,this.desired==='sleeping'?1.1:.6);}
  seek(t,state=this.state){this.state=state;this.time=t;this.elapsed=10;this.offset=null;this.pose=poseAt(t,state);this.previous={...this.pose};return this.pose;}
  step(dt,reduced=false){dt=Math.max(0,Math.min(dt,.05));if(!dt)return this.pose;if(reduced){this.pose=poseAt(0,this.dragging?'dragging':this.desired);this.previous={...this.pose};return this.pose;}this.previous={...this.pose};this.dt=dt;this.time+=dt;this.elapsed+=dt;
   if(this.clicking&&this.time>=DATA.states.done.duration){this.clicking=false;this.change(this.desired,.45);}
   if(!this.dragging&&!this.clicking&&this.desired==='idle'){if(this.state==='idle'&&this.time>=9)this.change('curiosity',.3);else if(this.state==='curiosity'&&this.time>=DURATION)this.change('idle',.4);}
   const p=poseAt(this.time,this.state),u=Math.min(1,this.elapsed/this.duration),h0=2*u*u*u-3*u*u+1,h1=u*u*u-2*u*u+u;
   if(this.offset&&u<1)for(const k in p)p[k]+=this.offset[k]*h0+this.velocity[k]*this.duration*h1;
   // Critically damped response uses bounded substeps; a stationary cursor settles.
   this.dragTarget*=Math.exp(-dt*6);for(let left=dt;left>1e-7;){const h=Math.min(left,1/120);this.dragSpeed+=(100*(this.dragTarget-this.dragAngle)-20*this.dragSpeed)*h;this.dragAngle+=this.dragSpeed*h;left-=h;}
   p.sway+=this.dragAngle;p.lid=Math.max(0,Math.min(1,p.lid));p.prop=Math.max(0,Math.min(1,p.prop));p.lift=Math.max(0,p.lift);p.tuck=Math.max(0,p.tuck);this.pose=p;return p;
  }
 }
 const api={DATA,DURATION,poseAt,jointsAt,render,Player};if(typeof module!=='undefined')module.exports=api;else root.OniRig=api;
})(globalThis);
