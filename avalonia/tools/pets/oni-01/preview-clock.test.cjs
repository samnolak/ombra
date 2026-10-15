const {test}=require('node:test'),a=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
test('preview passes real elapsed time to semantics and bounded time to visuals after a stall',()=>{
 const source=fs.readFileSync(__dirname+'/states-preview.template.html','utf8'),tick=source.match(/function tick\(now\)\{[^\n]+\}/)[0],calls=[];
 const env={raf:1,last:1000,playing:true,ready:true,time:0,speed:1,stageStep:(...args)=>calls.push(args),paint(){},paintStudies(){},ensureAnimation(){}};
 vm.runInNewContext(tick+';tick(6000)',env);
 a.deepEqual(calls,[[.1,5]]);a.equal(env.time,100);
});
test('hiding stops scheduling and restoring advances semantics while preserving an existing pause',()=>{
 const source=fs.readFileSync(__dirname+'/states-preview.template.html','utf8'),listener=source.match(/document\.addEventListener\('visibilitychange',\(\)=>\{[^\n]+\}\);/)[0];
 for(const wasPlaying of [true,false]){
  let callback,now=1000;const elapsed=[],cancelled=[];
  const env={last:10,hiddenPlaying:false,hiddenAt:null,playing:wasPlaying,raf:7,performance:{now:()=>now},document:{hidden:true,addEventListener:(name,fn)=>callback=fn},cancelAnimationFrame:id=>cancelled.push(id),paint(){},stagePaint(){},ensureAnimation(){},director:{step:dt=>elapsed.push(dt)}};
  vm.runInNewContext(listener,env);callback();a.equal(env.playing,false);a.equal(env.raf,null);a.deepEqual(cancelled,[7]);
  now=6000;env.document.hidden=false;callback();a.equal(env.playing,wasPlaying);a.deepEqual(elapsed,[5]);a.equal(env.hiddenAt,null);a.equal(env.last,null);
 }
});
