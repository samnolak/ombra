(function(root){
const clamp=(v,a,b)=>Math.max(a,Math.min(b,v));
// Exact critically damped update; retains position AND velocity when the target changes.
function spring(x,v,target,w,dt){const q=x-target,c=v+w*q,e=Math.exp(-w*dt);return [target+(q+c*dt)*e,(v-w*c*dt)*e]}
class PointerGesture{
 constructor(threshold=6){this.threshold=threshold;this.id=null;this.dragging=false;}
 down(id,x,y){if(this.id!==null)return false;this.id=id;this.x=x;this.y=y;this.dragging=false;return true;}
 move(id,x,y){if(id!==this.id)return 'none';if(this.dragging)return 'drag';if(Math.hypot(x-this.x,y-this.y)<this.threshold)return 'pending';this.dragging=true;return 'start';}
 up(id,cancel=false){if(id!==this.id||this.id===null)return 'none';const result=this.dragging?'drop':cancel?'cancel':'click';this.id=null;this.dragging=false;return result;}
}
class DragBody{
 constructor(x,y,bounds){this.x=x;this.y=y;this.vx=0;this.vy=0;this.angle=0;this.av=0;this.phase=0;this.pv=0;this.held=false;this.air=false;this.targetX=x;this.targetY=y;this.bounds=bounds;this.impacts=0;this.lastImpact=-10;this.clock=0;this.offsetX=0;this.offsetY=0;this.pointerId=null;this.ax=0;this.ay=0;this.length=52;this.impactSpeed=0;}
 grab(px,py,id=1){this.held=true;this.air=true;this.pointerId=id;this.offsetX=this.x-px;this.offsetY=this.y-py;this.targetX=this.x;this.targetY=this.y;}
 move(px,py){if(!this.held)return;this.targetX=clamp(px+this.offsetX,this.bounds.left,this.bounds.right);this.targetY=clamp(py+this.offsetY,this.bounds.top,this.bounds.floor);}
 release(){this.held=false;this.pointerId=null;/* Deliberately do not reset velocity, angle or phase. */}
 step(dt){if(!Number.isFinite(dt)||dt<=0)return;dt=Math.min(dt,.1);const n=Math.ceil(dt/(1/120)),h=dt/n;for(let i=0;i<n;i++)this.substep(h);}
 substep(dt){this.clock+=dt;const oldVx=this.vx,oldVy=this.vy;
  if(this.held){[this.x,this.vx]=spring(this.x,this.vx,this.targetX,48,dt);[this.y,this.vy]=spring(this.y,this.vy,this.targetY,48,dt)}
  else{this.vx*=Math.exp(-(this.air?2.3:10)*dt);this.x+=this.vx*dt;if(this.air){this.vy+=1100*dt;this.y+=this.vy*dt;}}
  if(this.x<this.bounds.left||this.x>this.bounds.right){this.x=clamp(this.x,this.bounds.left,this.bounds.right);this.vx*=-.16;}
  if(this.y<this.bounds.top){this.y=this.bounds.top;if(this.vy<0)this.vy*=-.16;}
  if(!this.held&&this.y>=this.bounds.floor){this.y=this.bounds.floor;if(this.vy>65&&this.clock-this.lastImpact>.2){this.impacts++;this.lastImpact=this.clock;this.impactSpeed=this.vy;}this.vy=0;this.air=false;}
  this.ax=clamp((this.vx-oldVx)/dt,-6500,6500);this.ay=clamp((this.vy-oldVy)/dt,-4000,4000);
  // Suspended mass below a moving nape. Canvas +angle puts the feet LEFT
  // of the grip: rightward acceleration must therefore produce +angle.
  // Constant velocity does not sustain lean. In free fall, gravity and pivot
  // acceleration cancel; angular momentum remains until landing.
  const suspended=this.held||this.air;
  const effectiveG=this.held?clamp(1100-this.ay,250,2400):this.air?0:1800;
  const force=this.held?this.ax*.28:0;
  const angularA=(force*Math.cos(this.angle)-effectiveG*Math.sin(this.angle))/this.length-(suspended?14:18)*this.av;
  this.av+=angularA*dt;this.angle+=this.av*dt;
  if(Math.abs(this.angle)>.18){this.angle=clamp(this.angle,-.18,.18);if(this.av*this.angle>0)this.av*=.15;}
  [this.phase,this.pv]=spring(this.phase,this.pv,this.held||this.air?1:0,13,dt);this.phase=clamp(this.phase,0,1);
 }
}
class SpiritFollower{
 constructor(x,y,seed=0){this.x=x;this.y=y;this.vx=0;this.vy=0;this.seed=seed;this.opacity=1;}
 step(dt,x,y,active=true){
  const n=Math.max(1,Math.ceil(Math.min(dt,.1)*120)),h=Math.min(dt,.1)/n;
  for(let i=0;i<n;i++){[this.x,this.vx]=spring(this.x,this.vx,x,5.5,h);[this.y,this.vy]=spring(this.y,this.vy,y,6,h);this.opacity+=(Number(active)-this.opacity)*(1-Math.exp(-h*7));}
 }
}
// Two inclined ellipses projected around the torso. Depth controls occlusion.
function orbit(t,index,size){const phase=t*4.4+index*Math.PI,tilt=index?-.56:.56,u=Math.cos(phase)*size*.58,v=Math.sin(phase)*size*.12;return {x:u*Math.cos(tilt)-v*Math.sin(tilt),y:u*Math.sin(tilt)+v*Math.cos(tilt),depth:Math.sin(phase)}}
function smooth(u){u=clamp(u,0,1);return u*u*(3-2*u)}
function easedPhase(time,duration){return smooth(clamp(time/duration,0,1))}
function transitionFrame(asset,phase){const sequence=asset.sequence||asset.frames.map((_,i)=>i);return sequence[Math.round(clamp(phase,0,1)*(sequence.length-1))]}
const api={DragBody,PointerGesture,SpiritFollower,spring,clamp,smooth,easedPhase,orbit,transitionFrame};if(typeof module==='object')module.exports=api;else root.OniInteraction=api;
})(typeof window==='object'?window:globalThis);
