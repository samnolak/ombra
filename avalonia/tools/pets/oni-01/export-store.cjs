const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const maxBytes=64*1024*1024;
function saveExport(directory,name,bytes){
 if(!/^oni-[a-z0-9-]+\.(png|webm)$/.test(name))throw Error('Invalid export name');
 if(!Buffer.isBuffer(bytes)||bytes.length<8||bytes.length>maxBytes)throw Error('Invalid export size');
 const signature=name.endsWith('.png')?Buffer.from([137,80,78,71,13,10,26,10]):Buffer.from([26,69,223,163]);
 if(!bytes.subarray(0,signature.length).equals(signature))throw Error('Invalid media signature');
 const sha256=crypto.createHash('sha256').update(bytes).digest('hex'),ext=path.extname(name),file=name.slice(0,-ext.length)+'-'+sha256.slice(0,12)+ext;
 fs.mkdirSync(directory,{recursive:true});const destination=path.join(directory,file);
 if(!fs.existsSync(destination))fs.writeFileSync(destination,bytes,{flag:'wx'});
 if(crypto.createHash('sha256').update(fs.readFileSync(destination)).digest('hex')!==sha256)throw Error('Export verification failed');
 return {file,sha256,bytes:bytes.length};
}
module.exports={saveExport,maxBytes};
