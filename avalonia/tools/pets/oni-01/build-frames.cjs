// ARCHIVED PASS ONLY. Current approved appearance: build-states.cjs.
// Measure source alpha and register the foot support. Never resize poses individually or alter artwork pixels.
const fs=require('node:fs'),path=require('node:path'),sharp=require('sharp');
(async()=>{
 const pngs=['frames/curiosity-v1.png','frames/curiosity-v2.png'].map(p=>fs.readFileSync(path.join(__dirname,p)));
 async function measure(png,atlas){
 const {data,info}=await sharp(png).ensureAlpha().raw().toBuffer({resolveWithObject:true});
 if(info.width%4||info.height%6)throw Error('Expected 4 by 6 sheet');
 const nominalW=info.width/4,nominalH=info.height/6,frames=[];
 const opaque=(x,y)=>data[(y*info.width+x)*4+3]>=32;
 function gutter(ideal,radius,clear){let best=null,start=null;for(let n=Math.floor(ideal-radius);n<=Math.ceil(ideal+radius);n++){if(clear(n)){if(start===null)start=n}else if(start!==null){if(!best||n-start>best[1]-best[0])best=[start,n-1];start=null}}if(start!==null&&(!best||ideal+radius-start>best[1]-best[0]))best=[start,Math.floor(ideal+radius)];if(!best||best[1]-best[0]<3)throw Error('No safe gutter');return Math.floor((best[0]+best[1])/2)}
 const ys=[0,...[1,2,3,4,5].map(k=>gutter(k*nominalH,nominalH*.12,y=>{for(let x=0;x<info.width;x++)if(opaque(x,y))return false;return true})),info.height];
 for(let i=0;i<24;i++){
  const row=Math.floor(i/4),sy=ys[row],ch=ys[row+1]-sy;
  const xs=[0,...[1,2,3].map(k=>gutter(k*nominalW,nominalW*.14,x=>{for(let y=sy;y<sy+ch;y++)if(opaque(x,y))return false;return true})),info.width];
  const sx=xs[i%4],cw=xs[i%4+1]-sx;
  let left=cw,right=-1,top=ch,bottom=-1,ink=0;
  const alpha=(x,y)=>data[((sy+y)*info.width+sx+x)*4+3];
  for(let y=0;y<ch;y++)for(let x=0;x<cw;x++)if(alpha(x,y)>=32){left=Math.min(left,x);right=Math.max(right,x);top=Math.min(top,y);bottom=Math.max(bottom,y);ink++;}
  if(ink<1000||left<2||top<2||right>cw-3||bottom>ch-3)throw Error('Empty or clipped frame '+(i+1));
  let footLeft=cw,footRight=-1;
  for(let y=bottom-10;y<=bottom;y++)for(let x=left;x<=right;x++)if(alpha(x,y)>150){footLeft=Math.min(footLeft,x);footRight=Math.max(footRight,x);}
  frames.push({atlas,sx,sy,cw,ch,left,right,top,bottom,anchorX:(footLeft+footRight)/2,anchorY:bottom,footSpan:footRight-footLeft,ink});
 }
 return frames;
 }
 const sources=await Promise.all(pngs.map(measure));
 // Reject the new sheet as a complete replacement: it changed unrelated poses.
 // Select only useful, visually inspected breakdowns. Never use the unwanted frontal head turn.
 const picks=Array.from({length:24},(_,i)=>[0,i]);
 picks[12]=[1,12];picks[13]=[1,13];picks[15]=[1,9];picks[17]=[1,17];picks[18]=[0,17];
 // Return through the same model drawings used at the opening: avoid neutral-pose model drift.
 picks[20]=[0,2];picks[21]=[0,1];picks[22]=[0,0];picks[23]=[0,0];
 const frames=picks.map(([atlas,index])=>({...sources[atlas][index],sourceFrame:index+1}));
 const durations=[360,75,90,130,130,100,85,85,85,85,100,300,55,55,65,120,100,100,100,100,130,120,160,320];
 const labels=['Покой','Услышал','Поднял подбородок','Присмотрелся','Наклонил голову','Решился','Поднимает руку','Тянется','Ещё ближе','Проверяет','Почти коснулся','Задержал руку','Неожиданность','Отдёргивает','Закрывается','Испугался','Выдохнул','Опускает руки','Возвращает вес','Расслабился','Держится уверенно','Оседает','Успокоился','Покой'];
 const spec={version:1,frames,height:Math.max(...frames.map(f=>f.bottom-f.top+1)),durations,labels};
 fs.writeFileSync(path.join(__dirname,'frames/registration.json'),JSON.stringify(spec,null,2)+'\n');
 let html=fs.readFileSync(path.join(__dirname,'frames-preview.template.html'),'utf8').replace('__ATLASES__',JSON.stringify(pngs.map(png=>'data:image/png;base64,'+png.toString('base64')))).replace('__SPEC__',JSON.stringify(spec));
 fs.writeFileSync(path.join(__dirname,'studies/rejected-drawn-preview-v2.html'),html);
 console.log(JSON.stringify({frames:frames.length,sourceAtlases:pngs.length,heightRange:[Math.min(...frames.map(f=>f.bottom-f.top)),Math.max(...frames.map(f=>f.bottom-f.top))],footSpanRange:[Math.min(...frames.map(f=>f.footSpan)),Math.max(...frames.map(f=>f.footSpan))],duration:durations.reduce((a,b)=>a+b)},null,2));
})().catch(e=>{console.error(e);process.exitCode=1});
