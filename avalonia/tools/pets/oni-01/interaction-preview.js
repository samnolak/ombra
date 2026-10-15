const stage=$('stage'),sc=stage.getContext('2d'),W=720,H=330;
const body=new OniInteraction.DragBody(360,284,{left:90,right:630,top:155,floor:284});
const transitionImages={};for(const id of ['pickup','question','workEntry','sleepEntry','thinkEntry','errorEntry','joyEntry']){transitionImages[id]=loadImage(assets[id].image)}
const transitionReady=Promise.all(Object.values(transitionImages).map(im=>im.decode()));
let fxOn=true,questionTarget=0,questionPhase=0,questionVelocity=0,stageTime=0,flameTime=0,demo=null,call=-10,impact=0,landing=-10,landingX=360,orbitMix=0,orbitV=0;
const orbitCenter={x:360,y:224};
const spirits=[new OniInteraction.SpiritFollower(310,216,0),new OniInteraction.SpiritFollower(410,246,1)];
function transition(ctx,id,phase,x,y,h){const a=assets[id],f=a.frames[OniInteraction.transitionFrame(a,phase)],s=h/a.referenceHeight;ctx.drawImage(transitionImages[id],f.sx,f.sy,f.cw,f.ch,x-f.ax*s,y-f.ay*s,f.cw*s,f.ch*s)}
function paintSpirits(front,h){
 for(let i=0;i<spirits.length;i++){
  const p=spirits[i],v=reduced?OniEffects.spiritPose(director.cue,director.cue==='sleeping'?3:0,i):(p.visual||OniEffects.spiritPose('idle',0,i));
  if((v.depth>0)!==front||v.alpha<.01)continue;
  fx.spirit(sc,i,p.fireTime||0,body.x+v.x*h,body.y+v.y*h,h*v.scale,v.alpha,reduced?0:v.lean,reduced);
 }
}
function stagePaint(){
 if(!ready)return;sc.setTransform(2,0,0,2,0,0);sc.clearRect(0,0,W,H);sc.fillStyle=light?'#eee8e2':'#181720';sc.fillRect(0,0,W,H);
 sc.strokeStyle=light?'#c5b5bf':'#49404e';sc.beginPath();sc.moveTo(30,body.bounds.floor+1);sc.lineTo(W-30,body.bounds.floor+1);sc.stroke();
 const h=size,neckY=body.y-h*.42;
 sc.fillStyle=light?'#392c3c28':'#0005';sc.beginPath();sc.ellipse(body.x,body.bounds.floor+3,h*.3,h*.035,0,0,Math.PI*2);sc.fill();
 if(fxOn){
  fx.draw(sc,director.cue,director.cueAge*1000,body.x,body.y,h,false,reduced,false);paintSpirits(false,h);

  
 }
 sc.save();sc.translate(body.x,neckY);sc.rotate(reduced?0:body.angle);sceneBody(sc,0,h*.42,h);sc.restore();
 if(fxOn){
  // Dust overlaps the boots in the foreground, then dissolves.
  if(!body.held&&!body.air)fx.event(sc,0,stageTime-landing,landingX,body.bounds.floor-8,h*1.15,.85);
  paintSpirits(true,h);fx.draw(sc,director.cue,director.cueAge*1000,body.x,body.y,h,true,reduced,false);
 }
 stage.dataset.mode=body.held?'held':body.air?'air':'ground';stage.dataset.x=body.x.toFixed(1);stage.dataset.y=body.y.toFixed(1);stage.dataset.vx=body.vx.toFixed(1);stage.dataset.angle=body.angle.toFixed(3);stage.dataset.phase=body.phase.toFixed(3);stage.dataset.feetX=(body.x-Math.sin(body.angle)*h*.42).toFixed(1);stage.dataset.fx=(body.held||body.air)?'crossed-orbits':'idle-spirits';
 $('dynamics').textContent=(body.held?'Захвачен':body.air?'В полёте':'На земле')+' · скорость '+Math.round(Math.hypot(body.vx,body.vy))+' px/с · наклон '+Math.round(body.angle*180/Math.PI)+'°';
 const qc=$('question').getContext('2d');qc.setTransform(2,0,0,2,0,0);qc.clearRect(0,0,220,200);if(fxOn&&questionPhase>.75)fx.draw(qc,'waiting',stageTime*1000,110,178,128,false,reduced);transition(qc,'question',questionPhase,110,178,128);if(fxOn&&questionPhase>.75)fx.draw(qc,'waiting',stageTime*1000,110,178,128,true,reduced);
 $('question').dataset.phase=questionPhase.toFixed(3);$('question').dataset.frame=String(OniInteraction.transitionFrame(assets.question,questionPhase)+1);
 if($('fxStudies').open){const c=$('fxStudy').getContext('2d');c.setTransform(2,0,0,2,0,0);c.clearRect(0,0,720,170);for(let i=0;i<3;i++){if(fxOn)fx.event(c,[0,2,3][i],reduced?.35:stageTime%2.3,120+i*240,73,55,.95);c.fillStyle='#bbb2c8';c.textAlign='center';c.font='13px system-ui';c.fillText(['Посадка','Ошибка','След рывка'][i],120+i*240,145)}}
}
function stageStep(dt,elapsed=dt){
 stageTime+=dt;sceneStep(elapsed);flameTime+=dt*(1+.7*orbitMix);
 if(demo){demo.t+=dt;const t=demo.t;if(t<.95){const u=OniInteraction.smooth(t/.95);body.move(demo.startX+(demo.turnX-demo.startX)*u,demo.startY+(180-demo.startY)*u)}else if(t<1.85){const u=OniInteraction.smooth((t-.95)/.9);body.move(demo.turnX+(demo.endX-demo.turnX)*u,180-25*u)}else{body.release();demo=null}}
 const previousX=body.x,previousY=body.y;
 body.length=size*.4;body.step(dt);
 if(body.impacts!==impact){impact=body.impacts;if(body.impactSpeed>160){landing=stageTime;landingX=body.x}}
 const moving=body.held||body.air;
 [orbitMix,orbitV]=OniInteraction.spring(orbitMix,orbitV,moving?1:0,18,dt);
 // Carry the orbit center with the body: no second positional spring.
 orbitCenter.x=body.x;orbitCenter.y=body.y-size*.47;
 for(let i=0;i<spirits.length;i++){
  const p=spirits[i],id=director.cue,t=reduced?(id==='sleeping'?3:0):id==='sleeping'?director.cueAge:stageTime;
  const target=OniEffects.spiritPose(id,t,i);
  if(!['sleeping','dragging'].includes(id)){const focus=Math.max(director.attention,director.hover?.25:0);target.x*=1-focus*.22;target.y-=focus*.035;}
  p.visual??={...target};p.visualV??={};
  for(const key of ['x','y','scale','alpha','depth','lean','tempo']){
   if(reduced){p.visual[key]=target[key];p.visualV[key]=0;}
   else [p.visual[key],p.visualV[key]]=OniInteraction.spring(p.visual[key],p.visualV[key]||0,target[key],id==='dragging'?30:14,dt);
  }
  p.fireTime=reduced?0:(p.fireTime||0)+dt*p.visual.tempo;
 }
 [questionPhase,questionVelocity]=OniInteraction.spring(questionPhase,questionVelocity,questionTarget,7,dt);
 stagePaint();
}
function point(e){const r=stage.getBoundingClientRect();return {x:(e.clientX-r.left)*W/r.width,y:(e.clientY-r.top)*H/r.height}}
const gesture=new OniInteraction.PointerGesture(6);
function hit(p){return Math.abs(p.x-body.x)<=size*.65&&p.y>=body.y-size*1.08&&p.y<=body.y+10}
function awaken(){if(!playing){playing=true;last=null}paint()}
stage.onpointerdown=e=>{const p=point(e);if(e.button!==0||body.held||!hit(p)||!gesture.down(e.pointerId,p.x,p.y))return;demo=null;stage.setPointerCapture(e.pointerId);awaken()};
stage.onpointermove=e=>{
 const p=point(e);director.hover=hit(p);
 if(gesture.id===null){if(director.hover){director.lastActive=director.clock;awaken()}return}
 const action=gesture.move(e.pointerId,p.x,p.y);
 if(action==='start'){body.grab(gesture.x,gesture.y,e.pointerId);director.event('drag-start');stage.style.cursor='grabbing'}
 if(action==='start'||action==='drag')body.move(p.x,p.y);
};
stage.onpointerleave=()=>{director.hover=false};
function releasePointer(e){
 const action=gesture.up(e.pointerId,e.type!=='pointerup');
 if(action==='drop')body.release();
 if(action==='click'){director.event('click');stage.dataset.lastInput='click';awaken()}
 if(action==='drop')stage.dataset.lastInput='drop';
 if(action==='none')return;stage.style.cursor='grab';if(stage.hasPointerCapture(e.pointerId))stage.releasePointerCapture(e.pointerId);
}
stage.onpointerup=releasePointer;stage.onpointercancel=releasePointer;stage.onlostpointercapture=releasePointer;
stage.tabIndex=0;stage.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();director.event('click');awaken()}};
function startDemo(direction){playing=true;last=null;body.grab(body.x,body.y);demo={t:0,direction,startX:body.x,startY:body.y,turnX:OniInteraction.clamp(body.x+direction*170,120,600),endX:OniInteraction.clamp(body.x-direction*150,120,600)};paint()}
$('demoLeft').onclick=()=>startDemo(-1);$('demoRight').onclick=()=>startDemo(1);
$('clickEffect').onclick=()=>{call=stageTime;director.event('click');playing=true;last=null;paint()};
$('questionToggle').onclick=()=>{questionTarget=1-questionTarget;$('questionToggle').textContent=questionTarget?'Вернуть руку':'Задать вопрос';playing=true;last=null;paint()};
$('fxToggle').onclick=()=>{fxOn=!fxOn;$('fxToggle').setAttribute('aria-pressed',String(fxOn));$('fxToggle').textContent=fxOn?'Эффекты: вкл.':'Эффекты: выкл.';paint();stagePaint()};
$('resetStage').onclick=()=>{body.release();demo=null;body.targetX=360;body.targetY=284;body.x=360;body.y=284;body.vx=body.vy=body.angle=body.av=body.phase=body.pv=0;body.air=false;landing=call=-10;orbitMix=orbitV=0;orbitCenter.x=360;orbitCenter.y=284-size*.47;spirits.forEach((p,i)=>{p.x=360+(i?1:-1)*size*.48;p.y=284-size*(i?.28:.51);p.vx=p.vy=0;p.opacity=1});questionPhase=questionVelocity=questionTarget=0;$('questionToggle').textContent='Задать вопрос';stagePaint()};
document.addEventListener('visibilitychange',()=>{if(document.hidden){gesture.up(gesture.id,true);body.release();demo=null;director.hover=false}});
$('recordDrag').onclick=()=>{
 if(!ready||$('recordDrag').disabled)return;
 if(!window.MediaRecorder){$('recordDrag').textContent='Запись недоступна';return}
 const stream=stage.captureStream(30),options=MediaRecorder.isTypeSupported('video/webm;codecs=vp9')?{mimeType:'video/webm;codecs=vp9'}:undefined,recorder=new MediaRecorder(stream,options),chunks=[];
 $('recordDrag').disabled=true;$('recordDrag').textContent='Запись…';
 recorder.ondataavailable=e=>chunks.push(e.data);
 recorder.onstop=()=>{const a=$('dragDownload');if(a.href.startsWith('blob:'))URL.revokeObjectURL(a.href);a.href=URL.createObjectURL(new Blob(chunks,{type:recorder.mimeType}));a.download='oni-drag-dynamics.webm';a.hidden=false;saveToProject(new Blob(chunks,{type:recorder.mimeType}),a.download,a);stream.getTracks().forEach(t=>t.stop());$('recordDrag').disabled=false;$('recordDrag').textContent='Записать перенос'};
 startDemo(-1);recorder.start();setTimeout(()=>startDemo(1),3500);setTimeout(()=>recorder.stop(),7000);
};

$('fxStudies').ontoggle=()=>stagePaint();
