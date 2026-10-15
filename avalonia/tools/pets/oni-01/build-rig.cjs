// ARCHIVED PASS ONLY. Current approved appearance: build-states.cjs.
const fs=require('node:fs'),path=require('node:path'),sharp=require('sharp');
(async()=>{
 const png=fs.readFileSync(path.join(__dirname,'rig/parts-v1.png'));
 const {data,info}=await sharp(png).ensureAlpha().raw().toBuffer({resolveWithObject:true});
 const opaque=(x,y)=>data[(y*info.width+x)*4+3]>=40;
 function gutter(ideal,radius,clear){let ranges=[],start=null;for(let p=Math.floor(ideal-radius);p<=Math.ceil(ideal+radius);p++){if(clear(p)){if(start===null)start=p}else if(start!==null){ranges.push([start,p-1]);start=null}}if(start!==null)ranges.push([start,Math.ceil(ideal+radius)]);ranges.sort((a,b)=>(b[1]-b[0])-(a[1]-a[0]));if(!ranges.length)throw Error('Missing gutter');return Math.floor((ranges[0][0]+ranges[0][1])/2);}
 const ys=[0,...[1,2,3].map(k=>gutter(info.height*k/4,info.height*.04,y=>{for(let x=0;x<info.width;x++)if(opaque(x,y))return false;return true})),info.height];
 const parts=[];for(let row=0;row<4;row++){
  const xs=[0,...[1,2,3].map(k=>gutter(info.width*k/4,info.width*.04,x=>{for(let y=ys[row];y<ys[row+1];y++)if(opaque(x,y))return false;return true})),info.width];
  for(let col=0;col<4;col++){let l=info.width,t=info.height,r=-1,b=-1;for(let y=ys[row];y<ys[row+1];y++)for(let x=xs[col];x<xs[col+1];x++)if(opaque(x,y)){l=Math.min(l,x);r=Math.max(r,x);t=Math.min(t,y);b=Math.max(b,y)}if(r<l)throw Error('Empty part');parts.push({x:l-2,y:t-2,w:r-l+5,h:b-t+5});}
 }
 const names=['head','chest','pelvis','near-upper-arm','near-forearm','near-open-hand','far-upper-arm','far-forearm','far-hand','near-thigh','near-shin-boot','far-thigh','far-shin-boot','near-curled-hand','head-surprised-unused','head-cheeky-unused'];
 const manifest=parts.map((p,i)=>({...p,name:names[i]}));fs.writeFileSync(path.join(__dirname,'rig/manifest.json'),JSON.stringify(manifest,null,2)+'\n');
 let html=fs.readFileSync(path.join(__dirname,'rig-preview.template.html'),'utf8');html=html.replace('__RIG_JS__',()=>fs.readFileSync(path.join(__dirname,'rig.js'),'utf8')).replace('__MOTION__',()=>fs.readFileSync(path.join(__dirname,'motion.json'),'utf8')).replace('__PARTS__',JSON.stringify(manifest)).replace('__RIG_ATLAS__','data:image/png;base64,'+png.toString('base64'));fs.writeFileSync(path.join(__dirname,'studies/rejected-rig-preview.html'),html);console.log('Rig preview built, 16 isolated source parts; 13 used, one consistent head.');
})().catch(e=>{console.error(e);process.exitCode=1});
