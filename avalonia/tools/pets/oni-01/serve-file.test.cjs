const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path'),{Writable}=require('node:stream');
const {serveFile}=require('./serve-file.cjs');
test('recorded video supports byte ranges for seeking without reloading the first frame',async()=>{
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'oni-range-')),file=path.join(dir,'video.webm');fs.writeFileSync(file,'0123456789');
 try{for(const [range,status,expected] of [['bytes=2-5',206,'2345'],['bytes=7-',206,'789'],['bytes=-3',206,'789'],['bytes=20-',416,''],[undefined,200,'0123456789']]){
  const chunks=[],headers={};let code=200,sent=false;const res=new Writable({write(chunk,enc,done){chunks.push(chunk);done()}});res.setHeader=(k,v)=>{assert.equal(sent,false,'headers already sent');headers[k]=v};res.writeHead=n=>{code=n;sent=true};
  const done=new Promise((resolve,reject)=>{res.on('finish',resolve);res.on('error',reject)});serveFile({headers:{range}},res,file,'video/webm');await done;
  assert.equal(code,status);assert.equal(Buffer.concat(chunks).toString(),expected);assert.equal(headers['Accept-Ranges'],'bytes');if(status===206)assert.equal(Number(headers['Content-Length']),expected.length);
 }}finally{fs.rmSync(dir,{recursive:true,force:true})}
});
