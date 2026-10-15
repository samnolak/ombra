// Production assets only. Review videos, authoring studies and HTML stay outside.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=__dirname,out=path.join(root,'runtime');
const spec=require('./frames/approved-v1/manifest.json'),assets=require('./transitions/manifest.json');
const files=[...new Set(['RUNTIME_README.md','runtime-contract.json','PNG_OPTIMIZATION.json','verify-package.cjs','HANDOFF_PLAN.md','states-player.cjs','scenario-player.cjs','interaction.cjs','effects-player.cjs','frames/approved-v1/manifest.json','transitions/manifest.json',...spec.states.map(s=>s.source),...Object.values(assets).map(a=>a.source)])].sort();
const records=[];
for(const file of files){const data=fs.readFileSync(path.join(root,file)),dest=path.join(out,file);fs.mkdirSync(path.dirname(dest),{recursive:true});fs.writeFileSync(dest,data);records.push({path:file,bytes:data.length,sha256:crypto.createHash('sha256').update(data).digest('hex')});}
fs.writeFileSync(path.join(out,'manifest.sha256.json'),JSON.stringify(records,null,2)+'\n');
console.log('Runtime: '+records.length+' files, '+records.reduce((s,r)=>s+r.bytes,0)+' bytes');
