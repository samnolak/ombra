// Legacy rig evidence only. Current UI is verified in the browser; see QA.md.
const {chromium}=require('playwright'),fs=require('node:fs'),path=require('node:path');
const {pathToFileURL}=require('node:url');
(async()=>{
 const b=await chromium.launch({channel:'chrome',headless:true});
 try{const p=await b.newPage({viewport:{width:1080,height:1100}});await p.goto(pathToFileURL(path.join(__dirname,'studies/rejected-rig-preview.html')).href);await p.locator('body[data-ready=true]').waitFor();
 const sheet=await p.evaluate(()=>{
  oniStudy.seek(0);const c=document.createElement('canvas');c.width=1200;c.height=960;const ctx=c.getContext('2d');ctx.fillStyle='#e9e3db';ctx.fillRect(0,0,c.width,c.height);
  oniStudy.starts.forEach((t,i)=>{const x=(i%4)*300,y=Math.floor(i/4)*320;ctx.save();ctx.translate(x+150,y+272);ctx.scale(1.8,1.8);ctx.translate(0,-128);OniRig.render(ctx,image,parts,OniRig.poseAt(t/1000));ctx.restore();ctx.fillStyle='#624753';ctx.font='14px system-ui';ctx.fillText((t/1000).toFixed(2)+' s · '+oniStudy.labels[i],x+18,y+304)});return c.toDataURL('image/png').split(',')[1];
 });fs.writeFileSync(path.join(__dirname,'studies/rig-contact-sheet.png'),Buffer.from(sheet,'base64'));
 const video=await p.evaluate(async()=>{
  const c=document.createElement('canvas');c.width=640;c.height=640;const ctx=c.getContext('2d');
  const stream=c.captureStream(60),mime=MediaRecorder.isTypeSupported('video/webm;codecs=vp9')?'video/webm;codecs=vp9':'video/webm';
  const recorder=new MediaRecorder(stream,{mimeType:mime,videoBitsPerSecond:3500000}),chunks=[];
  const done=new Promise(resolve=>{recorder.ondataavailable=e=>{if(e.data.size)chunks.push(e.data)};recorder.onstop=async()=>{const reader=new FileReader();reader.onload=()=>resolve(reader.result.split(',')[1]);reader.readAsDataURL(new Blob(chunks,{type:mime}))}});
  recorder.start();const start=performance.now();await new Promise(resolve=>{function frame(now){const t=Math.min(3.8,Math.max(0,(now-start-300)/1000));ctx.fillStyle='#e9e3db';ctx.fillRect(0,0,640,640);ctx.save();ctx.translate(320,535);ctx.scale(3.4,3.4);ctx.translate(0,-128);OniRig.render(ctx,image,parts,OniRig.poseAt(t));ctx.restore();ctx.fillStyle='#634653';ctx.font='18px system-ui';ctx.fillText('ONI-01 / Motion study',28,40);if(now-start<4500)requestAnimationFrame(frame);else resolve()}requestAnimationFrame(frame)});
  recorder.stop();const data=await done;stream.getTracks().forEach(t=>t.stop());return data;
 });fs.writeFileSync(path.join(__dirname,'studies/oni-motion-v2.webm'),Buffer.from(video,'base64'));
 console.log('Saved contact sheet and real-time 60 fps canvas recording.');
 }finally{await b.close()}
})().catch(e=>{console.error(e);process.exitCode=1});
