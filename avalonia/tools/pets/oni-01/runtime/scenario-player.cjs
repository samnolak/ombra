(function(root){
 const ids=['idle','thinking','working','waiting','joy','error','sleeping','dragging'];
 const durations={thinking:.7,working:1.3,waiting:.75,joy:.65,error:.6,sleeping:1.25,dragging:.65};
 const clamp=(x,a,b)=>Math.max(a,Math.min(b,x));
 class Director{
  constructor(clips){this.clips=clips;this.clock=0;this.lastActive=0;this.base='idle';this.flash=null;this.carried=false;this.state='idle';this.wanted='idle';this.phase=0;this.velocity=0;this.mode='loop';this.age=0;this.cue='idle';this.cueAge=0;this.settle=0;this.attention=0;this.hover=false;this.events=[];}
  log(kind){this.events.push({at:this.clock,kind,base:this.base});if(this.events.length>12)this.events.shift();}
  event(kind){
   if(kind==='drag-start'){this.carried=true;this.lastActive=this.clock;}
   else if(kind==='land'){this.carried=false;this.lastActive=this.clock;}
   else if(kind==='done'){if(!['waiting','error'].includes(this.base)){this.base='idle';this.flash={id:'joy',until:this.clock+8,visibleUntil:null};}this.lastActive=this.clock;}
   else if(kind==='tool-error'){this.flash={id:'error',until:this.clock+2.4};this.lastActive=this.clock;}
   else if(kind==='abort'){this.base='idle';this.flash=null;this.lastActive=this.clock;}
   else if(kind==='click'||kind==='type'){this.lastActive=this.clock;this.attention=1;}
   else if(kind==='sleep'){this.base='idle';this.flash=null;this.lastActive=this.clock-180;}
   else if(ids.includes(kind)&&!['dragging','joy','sleeping'].includes(kind)){this.base=kind;this.lastActive=this.clock;if(['waiting','error'].includes(kind)||(kind!=='idle'&&this.flash?.id==='joy'))this.flash=null;}
   else throw Error('Unknown event '+kind);
   this.log(kind);this.resolve();
  }
  target(){if(this.carried)return 'dragging';if(this.base==='waiting'||this.base==='error')return this.base;if(this.flash&&this.clock<this.flash.until)return this.flash.id;if(this.base!=='idle')return this.base;return this.clock-this.lastActive>=180?'sleeping':'idle';}
  resolve(){this.wanted=this.target();if(this.cue!==this.wanted){this.cue=this.wanted;this.cueAge=0;}}
  loopFrame(){const clip=this.clips[this.state];if(!clip)return 0;const t=(this.age*1000)%clip.duration;return [...clip.keys].reverse().find(k=>t>=k.start)?.frame||0;}
  step(dt){
   if(!Number.isFinite(dt)||dt<0)throw Error('Invalid delta');this.clock+=dt;
   if(this.base!=='idle')this.lastActive=this.clock;
   // Success must survive putting away the laptop; count the short celebration
   // from its visible loop, with an absolute deadline against stale reactions.
   if(this.flash?.id==='joy'&&this.state==='joy'&&this.mode==='loop'&&this.flash.visibleUntil===null){this.flash.visibleUntil=this.clock+2.4;this.flash.until=Math.min(this.flash.until,this.flash.visibleUntil);}
   if(this.flash&&this.clock>=this.flash.until)this.flash=null;
   this.resolve();this.cueAge+=dt;this.attention*=Math.exp(-dt*3);
   // Bound animation work after a stalled tab; do not skip across drawings.
   let remaining=Math.min(dt,.1);while(remaining>0){const h=Math.min(remaining,1/120);this.animate(h);remaining-=h;}
  }
  animate(dt){
   if(this.mode==='loop'){
    if(this.state===this.wanted){this.age+=dt;return;}
    if(this.state==='idle'){this.state=this.wanted;this.phase=0;this.velocity=0;this.mode='bridge';return;}
    // Return along adjacent loop drawings to its entry pose before exiting.
    const keys=this.clips[this.state].keys,t=(this.age*1000)%this.clips[this.state].duration;
    this.settleIndex=keys.findLastIndex(k=>t>=k.start);this.settleAge=0;this.settle=keys[this.settleIndex].frame;this.mode='settle';
   }
   if(this.mode==='settle'){
    if(this.wanted===this.state){const key=this.clips[this.state]?.keys.find(k=>k.frame===Math.round(this.settle));this.age=(key?.start||0)/1000;this.mode='loop';return;}
    this.settleAge+=dt;
    const clip=this.clips[this.state],keys=clip.keys;
    const neutral=clip.fullCycle?clip.neutralFrames.includes(this.settle):this.settleIndex===0;
    const interval=clip.fullCycle?keys[this.settleIndex].duration/1000:.08;
    if(this.settleAge>=interval&&!neutral){this.settleAge-=interval;this.settleIndex=clip.fullCycle?(this.settleIndex+1)%keys.length:Math.max(0,this.settleIndex-1);this.settle=keys[this.settleIndex].frame;}
    if(clip.fullCycle?clip.neutralFrames.includes(this.settle):this.settleIndex===0){this.phase=1;this.velocity=0;this.mode='bridge';}return;
   }
   const target=this.wanted===this.state?1:0,w=8/(durations[this.state]||.8);
   const q=this.phase-target,c=this.velocity+w*q,e=Math.exp(-w*dt);
   this.phase=clamp(target+(q+c*dt)*e,0,1);this.velocity=(this.velocity-w*c*dt)*e;
   if(Math.abs(this.phase-target)<.004&&Math.abs(this.velocity)<.025){
    this.phase=target;this.velocity=0;
    if(target===1){this.mode='loop';this.age=0;}else{this.state='idle';this.mode='loop';this.age=0;}
   }
  }
  sample(){return {state:this.state,mode:this.mode,phase:this.phase,frame:this.mode==='settle'?Math.round(this.settle):this.loopFrame(),cue:this.cue,cueAge:this.cueAge,clock:this.clock,base:this.base,carried:this.carried};}
 }
 const script=[['idle',2],['thinking',4],['working',5],['waiting',4],['working',4],['done',4],['idle',2],['sleep',4],['type',3],['working',3],['tool-error',4],['working',2],['abort',3],['error',4],['idle',3]];
 const interruptions=[['idle',.5],['working',.35],['waiting',.4],['error',.35],['thinking',.4],['waiting',1.8],['working',2],['drag-left',.4],['error',2.5],['sleep',2.2],['drag-right',.4],['waiting',2.6],['working',2.2],['done',4.5],['idle',1.5]];
 const api={Director,ids,script,interruptions};if(typeof module==='object')module.exports=api;else root.OniScenario=api;
})(typeof window==='object'?window:globalThis);
