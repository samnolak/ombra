const fs=require('node:fs'),path=require('node:path'),sharp=require('sharp');
async function sheet(file,cols,rows,anchor){
 const png=fs.readFileSync(path.join(__dirname,file)),{data,info}=await sharp(png).ensureAlpha().raw().toBuffer({resolveWithObject:true});
 const cw=Math.floor(info.width/cols),nominalH=info.height/rows,frames=[],ys=[0];
 for(let k=1;k<rows;k++){
  const a=Math.floor(k*nominalH-nominalH*.18),z=Math.ceil(k*nominalH+nominalH*.18);let best=k*nominalH,min=Infinity;
  for(let y=a;y<=z;y++){let n=0;for(let x=0;x<info.width;x++)if(data[(y*info.width+x)*4+3]>150)n++;const score=n+Math.abs(y-k*nominalH)*.01;if(score<min){min=score;best=y}}
  ys.push(Math.round(best));
 }ys.push(info.height);
 for(let i=0;i<cols*rows;i++){
  const row=Math.floor(i/cols),sx=i%cols*cw,sy=ys[row],ch=ys[row+1]-sy;let l=cw,r=0,t=ch,b=0,ink=0;
  const solid=(x,y)=>data[((sy+y)*info.width+sx+x)*4+3]>120;
  for(let y=0;y<ch;y++)for(let x=0;x<cw;x++)if(solid(x,y)){ink++;l=Math.min(l,x);r=Math.max(r,x);t=Math.min(t,y);b=Math.max(b,y)}
  let al=cw,ar=0;const from=anchor==='head'?t+30:b-12,to=anchor==='head'?Math.floor(t+(b-t)*.5):b;
  for(let y=from;y<=to;y++)for(let x=l;x<=r;x++)if(solid(x,y)){al=Math.min(al,x);ar=Math.max(ar,x)}
  // Measure the upper helmet only: raised hands reach the lower face band.
  let headL=cw,headR=-1;
  for(let y=Math.ceil(t+(b-t)*.18);y<=Math.floor(t+(b-t)*.30);y++)for(let x=l;x<=r;x++)if(solid(x,y)){headL=Math.min(headL,x);headR=Math.max(headR,x)}
  frames.push({ink,headWidth:headR-headL+1,sx,sy,cw,ch,l,r,t,b,ax:(al+ar)/2,ay:anchor==='head'?t:b,edgeContact:l===0||r===cw-1||t===0||b===ch-1});
 }
 return {source:file,image:'data:image/png;base64,'+png.toString('base64'),frames,referenceHeight:frames[0].b-frames[0].t+1};
}
module.exports=async()=>{
 const effects=await sheet('effects/atlas-v1.png',4,2,'ground');
 const pickup=await sheet('transitions/pickup.png',4,3,'head');
 const question=await sheet('transitions/question.png',4,3,'ground');
 question.sequence=Array.from({length:11},(_,i)=>i);
 question.excluded=[{frame:12,reason:'Abrupt head tilt at the endpoint; finish on the open palm in frame 11.'}];
 const flames=await sheet('effects/living-flames-v2.png',4,4,'ground');
 const vfx=await sheet('effects/event-vfx-v3.png',4,4,'ground');
 const assets={effects,pickup,question,flames,vfx};
 for(const id of ['work','sleep','think','error','joy'])assets[id+'Entry']=await sheet('transitions/'+id+'-entry.png',4,3,'ground');
 fs.writeFileSync(path.join(__dirname,'transitions/manifest.json'),JSON.stringify(Object.fromEntries(Object.entries(assets).map(([k,{image,...v}])=>[k,v])),null,2)+'\n');
 assets.joyEntry.sequence=Array.from({length:8},(_,i)=>i);
 return assets;
};
