const {test}=require('node:test'),a=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path');
test('project exporter verifies type and rejects traversal before writing',()=>{
 const modulePath=path.join(__dirname,'export-store.cjs');a.ok(fs.existsSync(modulePath),'project exporter is missing');
 const {saveExport}=require(modulePath),dir=fs.mkdtempSync(path.join(os.tmpdir(),'oni-export-test-'));
 try {const png=Buffer.from([137,80,78,71,13,10,26,10,0]);
  a.throws(()=>saveExport(dir,'../../escape.png',png));a.throws(()=>saveExport(dir,'oni-test.webm',png));a.equal(fs.readdirSync(dir).length,0);
  const result=saveExport(dir,'oni-test.png',png);a.deepEqual(fs.readFileSync(path.join(dir,result.file)),png);
  const again=saveExport(dir,'oni-test.png',png);a.equal(again.file,result.file);a.equal(fs.readdirSync(dir).length,1);
 }finally{fs.rmSync(dir,{recursive:true,force:true})}
});
