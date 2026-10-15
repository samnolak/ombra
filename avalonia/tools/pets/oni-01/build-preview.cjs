// ARCHIVED PASS ONLY. Current approved appearance: build-states.cjs.
// Build the offline pose-timing study. Requires sharp from the existing toolchain.
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');
(async () => {
  const png = fs.readFileSync(path.join(__dirname, 'studies/blocking-atlas-v4.png'));
  const {data, info} = await sharp(png).ensureAlpha().raw().toBuffer({resolveWithObject:true});
  if (info.width % 4 || info.height % 3) throw new Error('Expected exact 4 x 3 cells');
  const cw=info.width/4, ch=info.height/3;
  const opaque=(x,y)=>data[(y*info.width+x)*4+3]>=32;
  function gutter(ideal,radius,isClear){
    let best=null,start=null;
    for(let p=Math.floor(ideal-radius);p<=Math.ceil(ideal+radius);p++){
      if(isClear(p)){if(start===null)start=p}
      else if(start!==null){const end=p-1;if(!best||end-start>best[1]-best[0])best=[start,end];start=null}
    }
    if(start!==null){const end=Math.ceil(ideal+radius);if(!best||end-start>best[1]-best[0])best=[start,end]}
    if(!best||best[1]-best[0]<4)throw new Error('No clear gutter near '+ideal);
    return Math.floor((best[0]+best[1])/2);
  }
  const ys=[0,...[1,2].map(k=>gutter(k*ch,ch*.18,y=>{for(let x=0;x<info.width;x++)if(opaque(x,y))return false;return true})),info.height];
  const frames=[];
  for(let i=0;i<12;i++){
    const row=Math.floor(i/4),sy=ys[row],h=ys[row+1]-sy;
    const xs=[0,...[1,2,3].map(k=>gutter(k*cw,cw*.18,x=>{for(let y=sy;y<sy+h;y++)if(opaque(x,y))return false;return true})),info.width];
    const sx=xs[i%4],w=xs[i%4+1]-sx;
    let left=w,top=h,right=-1,bottom=-1;
    for(let y=0;y<h;y++)for(let x=0;x<w;x++){
      if(data[((sy+y)*info.width+sx+x)*4+3]<32)continue;
      left=Math.min(left,x);right=Math.max(right,x);top=Math.min(top,y);bottom=Math.max(bottom,y);
    }
    if(right<0)throw new Error(`Empty cell ${i}`);
    if(left<2||right>w-3||top<2||bottom>h-3)throw new Error(`Clipped cell ${i+1}`);
    // Register the common support centre, without resizing individual poses.
    let footLeft=w,footRight=0;
    for(let y=bottom-14;y<=bottom;y++)for(let x=left;x<=right;x++){
      if(data[((sy+y)*info.width+sx+x)*4+3]>100){footLeft=Math.min(footLeft,x);footRight=Math.max(footRight,x);}
    }
    frames.push({sx,sy,cw:w,ch:h,left,top,right,bottom,anchorX:(footLeft+footRight)/2,anchorY:bottom});
  }
  const spec={frames,height:Math.max(...frames.map(f=>f.bottom-f.top+1))};
  fs.writeFileSync(path.join(__dirname,'studies/blocking-registration.json'),JSON.stringify(spec,null,2)+'\n');
  const html=fs.readFileSync(path.join(__dirname,'preview.template.html'),'utf8')
    .replace('__ATLAS__','data:image/png;base64,'+png.toString('base64'))
    .replace('__SPEC__',JSON.stringify(spec));
  fs.writeFileSync(path.join(__dirname,'studies/blocking-preview.html'),html);
  console.log('Built preview.html; 12 nonempty cells, clear gutters, shared scale.');
})().catch(e=>{console.error(e);process.exitCode=1;});
