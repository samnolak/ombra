// Register whole drawings. No bone rig, morphing, per-frame scale or pixel alteration.
const fs=require('node:fs'),path=require('node:path'),sharp=require('sharp'),crypto=require('node:crypto');
const {timeline}=require('./states-player.cjs');
const rows=[
 ['idle','Обычный','Спокойно замечает что-то рядом',125,1700,650,0],
 ['joy','Радость','Кулачки у щёк, плавное покачивание торса; таз и стопы неподвижны',80,900,200,1],
 ['working','Работа','Печатает и проверяет результат',90,700,450,2],
 ['thinking','Размышление','Ищет ответ, удерживая мысль',120,1100,800,3],
 ['waiting','Вопрос / ожидание','Открытая ладонь, ждёт ответа',120,1300,950,4],
 ['sleeping','Сон','Закрытые глаза, медленное оседание головы',190,1500,950,5],
 ['error','Ошибка','Поднимает плечи и замирает',85,950,700,6],
 ['dragging','Перетаскивание','Ноги свободно покачиваются в подвесе',90,250,250,7]
];
const median=ns=>[...ns].sort((a,b)=>a-b)[Math.floor(ns.length/2)];
(async()=>{
 const poses=JSON.parse(fs.readFileSync(path.join(__dirname,'studies/miniature-style-v3.bounds.json'))),referenceHeight=Math.max(...poses.map(p=>p.h));
 const assets=await require('./build-effects.cjs')();
 const bridgeIds={working:'workEntry',sleeping:'sleepEntry',thinking:'thinkEntry',error:'errorEntry',joy:'joyEntry',waiting:'question',dragging:'pickup'};
 const states=[],images=[];
 for(const [id,label,intent,step,hold,apex,pose] of rows){
  const shared=id!=='joy'?assets[bridgeIds[id]]:null;
  const source=shared?shared.source:'frames/approved-v1/'+(id==='joy'?'joy-torso-v11':id)+'.png',png=fs.readFileSync(path.join(__dirname,source));
  const {data,info}=await sharp(png).ensureAlpha().raw().toBuffer({resolveWithObject:true});
  const cols=id==='joy'?8:4,rows=id==='joy'?8:3,count=cols*rows,cw=info.width/cols,nominalH=info.height/rows;if(!Number.isInteger(cw)||!Number.isInteger(nominalH))throw Error('Invalid grid '+id);
  const ys=[0];
  for(let k=1;k<rows;k++){
   let start=null,ranges=[];const a=Math.floor(k*nominalH-nominalH*.18),z=Math.ceil(k*nominalH+nominalH*.18);
   for(let y=a;y<=z;y++){let clear=true;for(let x=0;x<info.width;x++)if(data[(y*info.width+x)*4+3]>150){clear=false;break}
    if(clear&&start===null)start=y;if(!clear&&start!==null){ranges.push([start,y-1]);start=null;}}
   if(start!==null)ranges.push([start,z]);ranges.sort((a,b)=>(b[1]-b[0])-(a[1]-a[0]));
   if(!ranges.length)throw Error('No row gutter '+id);ys.push(Math.floor((ranges[0][0]+ranges[0][1])/2));
  }ys.push(info.height);
  const frames=[];
  for(let i=0;i<count;i++){
   const row=Math.floor(i/cols),sx=i%cols*cw,sy=ys[row],ch=ys[row+1]-sy;
   const alpha=(x,y)=>data[((sy+y)*info.width+sx+x)*4+3];
   let l=cw,r=-1,t=ch,b=-1,ink=0;
   for(let y=0;y<ch;y++)for(let x=0;x<cw;x++)if(alpha(x,y)>150){l=Math.min(l,x);r=Math.max(r,x);t=Math.min(t,y);b=Math.max(b,y);ink++;}
   let fl=cw,fr=-1;
   for(let y=b-12;y<=b;y++)for(let x=l;x<=r;x++)if(alpha(x,y)>150){fl=Math.min(fl,x);fr=Math.max(fr,x);}
   let ax=(fl+fr)/2,ay=b;
   if(id==='dragging'){
    let hl=cw,hr=-1;for(let y=t+30;y<t+(b-t)*.5;y++)for(let x=l;x<=r;x++)if(alpha(x,Math.floor(y))>150){hl=Math.min(hl,x);hr=Math.max(hr,x);}
    ax=(hl+hr)/2;ay=t;
   }
   // Measure the upper helmet only: raised hands reach the lower face band.
  let headL=cw,headR=-1;
   for(let y=Math.ceil(t+(b-t)*.18);y<=Math.floor(t+(b-t)*.30);y++)for(let x=l;x<=r;x++)if(alpha(x,y)>150){headL=Math.min(headL,x);headR=Math.max(headR,x)}
   frames.push({headWidth:headR-headL+1,sx,sy,cw,ch,left:l,right:r,top:t,bottom:b,anchorX:ax,anchorY:ay,ink,sourceFrame:i+1,clipped:l<2||r>cw-3||t<2||b>ch-3});
  }
  if(shared)frames.splice(0,frames.length,...shared.frames.map((f,i)=>({headWidth:f.headWidth,sx:f.sx,sy:f.sy,cw:f.cw,ch:f.ch,left:f.l,right:f.r,top:f.t,bottom:f.b,anchorX:f.ax,anchorY:f.ay,ink:f.ink,sourceFrame:i+1,clipped:f.edgeContact})));
  const accepted=frames.map((f,i)=>i).filter(i=>!frames[i].clipped);
  if(accepted.length<6)throw Error('Too many clipped frames: '+id);
  const measuredHeight=median(accepted.map(i=>frames[i].bottom-frames[i].top+1));
  const scale=poses[pose].h/referenceHeight/measuredHeight;
  const endpoint=shared?(shared.sequence||shared.frames.map((_,i)=>i)).at(-1):null;
  const loopFrames=shared?[endpoint,endpoint-1,endpoint-2,endpoint-3].filter(i=>accepted.includes(i)):accepted;
  if(shared&&loopFrames.length<3)throw Error('Insufficient intact settling drawings: '+id);
  let clip=timeline(loopFrames,step,hold,apex);
  if(id==='joy')clip={keys:accepted.map((frame,i)=>({frame,start:i*2800/count,duration:2800/count})),duration:2800,fullCycle:true,neutralFrames:[0,32]};
  states.push({id,label,intent,source,sha256:crypto.createHash('sha256').update(png).digest('hex'),frames,accepted,rejected:frames.filter(f=>f.clipped).map(f=>f.sourceFrame),scale,anchor:id==='dragging'?'head':'ground',clip});
  images.push('/'+source);
 }
 const spec={version:1,appearance:'approved/character-states.png',appearanceApproved:true,animationApproved:false,integrationAllowed:false,states};
 const headWidth=median(states[0].frames.map(f=>f.headWidth))*states[0].scale;
 spec.registration={method:'constant-per-sheet-head-width',headWidth};
 for(const state of states){
  state.registration={headWidth:median(state.accepted.map(i=>state.frames[i].headWidth))};
  state.scale=headWidth/state.registration.headWidth;
  state.staticFrame=state.id==='sleeping'?state.accepted.at(-1):state.clip.keys[0].frame;
  const bridge=assets[bridgeIds[state.id]];if(!bridge)continue;
  bridge.registration={headWidth:median(bridge.frames.map(f=>f.headWidth))};
  bridge.referenceHeight=bridge.registration.headWidth/headWidth;
  state.entry=bridgeIds[state.id];
  if(state.id!=='joy'){state.registration.headWidth=bridge.registration.headWidth;state.scale=1/bridge.referenceHeight;state.staticFrame=state.clip.keys[0].frame;state.sharedEndpoint=true;}
 }
 fs.writeFileSync(path.join(__dirname,'transitions/manifest.json'),JSON.stringify(Object.fromEntries(Object.entries(assets).map(([k,{image,...v}])=>[k,v])),null,2)+'\n');
 fs.writeFileSync(path.join(__dirname,'frames/approved-v1/manifest.json'),JSON.stringify(spec,null,2)+'\n');
 for(const asset of Object.values(assets))asset.image='/'+asset.source;
 const html=fs.readFileSync(path.join(__dirname,'states-preview.template.html'),'utf8').replace('__SPEC__',JSON.stringify(spec)).replace('__IMAGES__',JSON.stringify(images)).replace('__ASSETS__',JSON.stringify(assets)).replace('__PLAYER__',()=>fs.readFileSync(path.join(__dirname,'states-player.cjs'),'utf8')).replace('__INTERACTION__',()=>fs.readFileSync(path.join(__dirname,'interaction.cjs'),'utf8')).replace('__FX__',()=>fs.readFileSync(path.join(__dirname,'effects-player.cjs'),'utf8')).replace('__SCENARIO__',()=>fs.readFileSync(path.join(__dirname,'scenario-player.cjs'),'utf8')).replace('__SCENE_UI__',()=>fs.readFileSync(path.join(__dirname,'scenario-preview.js'),'utf8')).replace('__STAGE__',()=>fs.readFileSync(path.join(__dirname,'interaction-preview.js'),'utf8'));
 fs.writeFileSync(path.join(__dirname,'preview.html'),html);
 fs.writeFileSync(path.join(__dirname,'studies/approved-animation-preview.html'),'<!doctype html><meta charset="utf-8"><meta http-equiv="refresh" content="0;url=/"><a href="/">Открыть актуальный просмотрщик</a>');
 console.log(JSON.stringify(states.map(s=>({id:s.id,frames:s.accepted.length,rejected:s.rejected,duration:s.clip.duration})),null,2));
})();
