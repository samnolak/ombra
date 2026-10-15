// Rebuild a self-contained candidate. Artistic acceptance remains an explicit gate.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=__dirname,out=path.join(root,'handoff-candidate');
const spec=JSON.parse(fs.readFileSync(path.join(root,'frames/approved-v1/manifest.json')));
const assets=JSON.parse(fs.readFileSync(path.join(root,'transitions/manifest.json')));
const evidence=['studies/seams-v11-2026-10-15.png','studies/light-effects-0.4.png','studies/landing-light-3.2.png','studies/reduced-sleep.png','studies/paused-working.png','exports/oni-interruptions-bfccdf5f20da.webm','exports/oni-scenario-7e5bf55fedc1.webm','exports/oni-drag-dynamics-bc5ffe2ebe50.webm','exports/oni-approved-states-4a043f9b8e79.webm','exports/oni-approved-states-fadd1cec06ef.png','studies/miniatures-32-light.png','studies/miniatures-64-light.png','exports/oni-size-comparison-5c6daea3a72c.png'];
const tests=['states','interaction','input','scenario','registration','export-store','serve-file','preview-clock'].map(name=>name+'.test.cjs');
const files=[...new Set([...evidence,...tests,'states-preview.template.html','studies/playback-review.html','PNG_OPTIMIZATION.json','OPTIMIZATION.md','RUNTIME_README.md','HANDOFF.md','AGENT_TASK.md','README.md','HANDOFF_PLAN.md','REVIEW.md','runtime-contract.json','scenario-preview.js','interaction-preview.js','joy-torso.cjs','approved/character-states.png','preview.html','serve-preview.cjs','export-store.cjs','serve-file.cjs','verify-package.cjs','states-player.cjs','scenario-player.cjs','interaction.cjs','effects-player.cjs','frames/approved-v1/manifest.json','transitions/manifest.json',...spec.states.map(s=>s.source),...Object.values(assets).map(a=>a.source)])].sort();
const records=files.map(file=>{
 const data=fs.readFileSync(path.join(root,file));return {path:file,bytes:data.length,sha256:crypto.createHash('sha256').update(data).digest('hex')};
});
// Remove only unchanged files owned by the previous generated inventory.
const oldIndex=path.join(out,'manifest.sha256.json');
if(fs.existsSync(oldIndex))for(const old of JSON.parse(fs.readFileSync(oldIndex))){
 if(files.includes(old.path))continue;
 if(path.isAbsolute(old.path)||old.path.split('/').includes('..'))throw Error('Unsafe previous inventory path');
 const obsolete=path.join(out,old.path);if(!fs.existsSync(obsolete))continue;
 if(crypto.createHash('sha256').update(fs.readFileSync(obsolete)).digest('hex')!==old.sha256)throw Error('Modified candidate file; preserve manually: '+old.path);
 fs.unlinkSync(obsolete);
}
for(const r of records){const dest=path.join(out,r.path);fs.mkdirSync(path.dirname(dest),{recursive:true});fs.copyFileSync(path.join(root,r.path),dest);}
fs.writeFileSync(path.join(out,'manifest.sha256.json'),JSON.stringify(records,null,2)+'\n');
console.log('Candidate written: '+out+' ('+records.length+' files). Not an artistic approval.');
