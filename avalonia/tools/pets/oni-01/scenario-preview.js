// Event rehearsal uses the same whole-frame renderer and physical drag body.
const director=new OniScenario.Director(Object.fromEntries(spec.states.map(s=>[s.id,s.clip])));
const bridgeIds={working:'workEntry',sleeping:'sleepEntry',thinking:'thinkEntry',error:'errorEntry',joy:'joyEntry',waiting:'question',dragging:'pickup'};
let rehearsal=null,scenarioRecording=false;
const labels={idle:'Покой',thinking:'Думает',working:'Работает',waiting:'Нужен ответ',done:'Успех',error:'Сбой', 'tool-error':'Ошибка инструмента',abort:'Отмена',sleep:'Уснуть',type:'Ввод',click:'Клик'};
for(const [event,label] of Object.entries(labels)){
 const button=document.createElement('button');button.textContent=label;button.dataset.event=event;
 button.onclick=()=>{rehearsal=null;director.event(event);playing=true;last=null;paint()};$('events').append(button);
}
function sceneBody(ctx,x,y,h){
 const s=director.sample(),si=spec.states.findIndex(q=>q.id===s.state);
 if(reduced){const cue=spec.states.findIndex(q=>q.id===s.cue);raw(ctx,cue,spec.states[cue].staticFrame??spec.states[cue].clip.keys[0].frame,x,y,h);return}
 if(s.mode==='bridge'){
  const id=bridgeIds[s.state],a=assets[id];
  if(id)transition(ctx,id,s.phase,x,id==='pickup'?y-h*.98:y,h);
 }else raw(ctx,si,s.frame,x,y,h);
}
function sceneStep(dt){
 if(rehearsal){rehearsal.age+=dt;const beat=rehearsal.sequence[rehearsal.index];if(rehearsal.age>=beat[1]){rehearsal.age-=beat[1];rehearsal.index++;if(rehearsal.index>=rehearsal.sequence.length){rehearsal=null;$('runScenario').textContent='Пройти сценарий'}else rehearsalEvent(rehearsal.sequence[rehearsal.index][0]);}}
 const carried=body.held||body.air;if(carried!==director.carried)director.event(carried?'drag-start':'land');
 director.step(dt);
 const s=director.sample();$('scenarioStatus').textContent='Событие: '+(labels[s.base]||s.base)+' · действие: '+spec.states.find(q=>q.id===s.cue).label+' · '+(s.mode==='loop'?'основная поза':s.mode==='bridge'?'переход':'завершение жеста');
 stage.dataset.state=s.state;stage.dataset.cue=s.cue;stage.dataset.transition=s.mode;stage.dataset.transitionPhase=s.phase.toFixed(3);
}
function rehearsalEvent(kind){if(kind==='drag-left'||kind==='drag-right')startDemo(kind==='drag-left'?-1:1);else director.event(kind)}
function runScenario(sequence=OniScenario.script){rehearsal={index:0,age:0,sequence};rehearsalEvent(sequence[0][0]);playing=true;last=null;$('runScenario').textContent='Остановить сценарий';paint()}
$('runScenario').onclick=()=>{if(rehearsal){rehearsal=null;$('runScenario').textContent='Пройти сценарий'}else runScenario()};
$('reducedToggle').setAttribute('aria-pressed',String(reduced));
$('reducedToggle').onclick=()=>{reduced=!reduced;$('reducedToggle').setAttribute('aria-pressed',String(reduced));paint();stagePaint()};
function recordSequence(sequence,filename,button){
 if(!ready||scenarioRecording||!window.MediaRecorder)return;
 const stream=stage.captureStream(30),r=new MediaRecorder(stream),chunks=[];
 scenarioRecording=true;button.disabled=true;r.ondataavailable=e=>chunks.push(e.data);
 r.onstop=()=>{const a=$('scenarioDownload');if(a.href.startsWith('blob:'))URL.revokeObjectURL(a.href);a.href=URL.createObjectURL(new Blob(chunks,{type:r.mimeType}));a.download=filename;a.hidden=false;saveToProject(new Blob(chunks,{type:r.mimeType}),a.download,a);stream.getTracks().forEach(t=>t.stop());scenarioRecording=false;button.disabled=false};
 runScenario(sequence);r.start();setTimeout(()=>r.stop(),sequence.reduce((n,b)=>n+b[1],0)*1000+1000);
}
$('recordScenario').onclick=()=>recordSequence(OniScenario.script,'oni-scenario.webm',$('recordScenario'));
$('recordInterruptions').onclick=()=>recordSequence(OniScenario.interruptions,'oni-interruptions.webm',$('recordInterruptions'));
const seamCanvases=[];
for(const [state,id] of Object.entries(bridgeIds)){
 const c=document.createElement('canvas');c.width=440;c.height=400;c.style.width='220px';c.style.height='200px';c.setAttribute('aria-label','Стык: '+state);$('seams').append(c);seamCanvases.push({c,state,id});
}
function paintSizeBoard(ctx,stateId=director.cue){
 ctx.setTransform(2,0,0,2,0,0);ctx.fillStyle=light?'#eee8e2':'#24212b';ctx.fillRect(0,0,720,200);
 const id=spec.states.findIndex(q=>q.id===stateId),state=spec.states[id];
 ctx.font='13px system-ui';ctx.textAlign='left';ctx.fillStyle=light?'#302936':'#eeeaf3';ctx.fillText(state.label,12,18);
 [32,48,64,80,96,128].forEach((h,i)=>{const x=[50,140,240,350,470,620][i],y=160,frame=state.staticFrame??state.clip.keys[0].frame;
  if(fxOn)fx.draw(ctx,state.id,0,x,y,h,false,true);raw(ctx,id,frame,x,y,h);if(fxOn)fx.draw(ctx,state.id,0,x,y,h,true,true);
  ctx.font='13px system-ui';ctx.textAlign='center';ctx.fillStyle=light?'#302936':'#eeeaf3';ctx.fillText(h+' px',x,187);
 });
}
function paintStudies(){
 if(!ready||!$('sizeStudies').open)return;paintSizeBoard($('sizeStudy').getContext('2d'));
 for(const {c,state,id} of seamCanvases){const ctx=c.getContext('2d');ctx.setTransform(2,0,0,2,0,0);ctx.fillStyle=light?'#eee8e2':'#24212b';ctx.fillRect(0,0,220,200);const si=spec.states.findIndex(q=>q.id===state);
  transition(ctx,id,1,55,id==='pickup'?156-96*.98:156,96);raw(ctx,si,spec.states[si].clip.keys[0].frame,165,156,96);
  ctx.fillStyle=light?'#302936':'#eeeaf3';ctx.textAlign='center';ctx.font='11px system-ui';ctx.fillText(spec.states[si].label,110,18);ctx.fillText('Конец входа',55,183);ctx.fillText('Начало цикла',165,183);
 }
}
$('sizeStudies').ontoggle=paintStudies;
$('exportSizes').onclick=()=>{const c=document.createElement('canvas'),cell=document.createElement('canvas');c.width=cell.width=1440;cell.height=400;c.height=400*spec.states.length;for(const [i,state] of spec.states.entries()){paintSizeBoard(cell.getContext('2d'),state.id);c.getContext('2d').drawImage(cell,0,i*400)}c.toBlob(b=>download(b,'oni-size-comparison.png'))};
