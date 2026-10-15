const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const records=JSON.parse(fs.readFileSync(path.join(__dirname,'manifest.sha256.json'),'utf8'));
for(const r of records){
 if(path.isAbsolute(r.path)||r.path.split('/').includes('..'))throw Error('Unsafe path '+r.path);
 const data=fs.readFileSync(path.join(__dirname,r.path));
 if(data.length!==r.bytes||crypto.createHash('sha256').update(data).digest('hex')!==r.sha256)throw Error('Changed or corrupt: '+r.path);
}
console.log('Verified '+records.length+' package files. Artistic acceptance is recorded separately in HANDOFF_PLAN.md.');
