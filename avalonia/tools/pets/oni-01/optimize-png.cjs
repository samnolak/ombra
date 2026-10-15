// Lossless delivery optimization: keep dimensions and EVERY decoded RGBA byte.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),sharp=require('sharp');
const root=__dirname,hash=b=>crypto.createHash('sha256').update(b).digest('hex');
(async()=>{
 const spec=require('./frames/approved-v1/manifest.json'),assets=require('./transitions/manifest.json');
 const files=[...new Set([...spec.states.map(s=>s.source),...Object.values(assets).map(a=>a.source)])];
 const report=[];
 for(const file of files){
  const p=path.join(root,file),input=fs.readFileSync(p),decoded=await sharp(input).ensureAlpha().raw().toBuffer({resolveWithObject:true});
  const candidate=await sharp(input).png({compressionLevel:9,adaptiveFiltering:true,palette:false}).toBuffer();
  const after=await sharp(candidate).ensureAlpha().raw().toBuffer();
  if(!decoded.data.equals(after))throw Error('Pixel mismatch: '+file);
  const output=candidate.length<input.length?candidate:input;
  if(output!==input)fs.writeFileSync(p,output);
  report.push({file,before:input.length,after:output.length,width:decoded.info.width,height:decoded.info.height,rgbaBytes:decoded.data.length,pixelSha256:hash(decoded.data),pixelsIdentical:true});
 }
 fs.writeFileSync(path.join(root,'PNG_OPTIMIZATION.json'),JSON.stringify(report,null,2)+'\n');
 console.log(JSON.stringify({files:report.length,before:report.reduce((s,r)=>s+r.before,0),after:report.reduce((s,r)=>s+r.after,0),uniqueDecodedRgbaBytes:report.reduce((s,r)=>s+r.rgbaBytes,0)},null,2));
})().catch(e=>{console.error(e);process.exitCode=1});
