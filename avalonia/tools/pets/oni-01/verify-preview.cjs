// Legacy rig evidence only. Current UI is verified in the browser; see QA.md.
const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const path=require('node:path');
const {pathToFileURL}=require('node:url');
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1080,height:1100},deviceScaleFactor:1});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.join(__dirname,'studies/rejected-rig-preview.html')).href);
  await page.locator('body[data-ready="true"]').waitFor();
  await page.getByRole('button',{name:'Пауза',exact:true}).click();
  const t=await page.evaluate(()=>oniStudy.time);await page.waitForTimeout(100);
  assert.equal(await page.evaluate(()=>oniStudy.time),t,'pause freezes time');
  for(let i=0;i<12;i++){
   await page.getByRole('button',{name:new RegExp(`^Поза ${i+1}:`)}).click();
   assert.equal(await page.locator('body').getAttribute('data-frame'),String(i));
   const pixels=await page.locator('#hero').evaluate(c=>{const p=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let n=0;for(let j=3;j<p.length;j+=4)if(p[j]>32)n++;return n});
   assert.ok(pixels>10000,`pose ${i+1} renders`);
  }
  await page.locator('#speed').selectOption('0.25');
  await page.getByRole('button',{name:'↻ Проиграть этюд'}).click();
  await page.waitForTimeout(400);
  const slow=await page.evaluate(()=>oniStudy.time);assert.ok(slow>40&&slow<240,'quarter speed advances slowly');
  await page.evaluate(()=>oniStudy.seek(3800));assert.equal(await page.locator('body').getAttribute('data-frame'),'11');
  await page.getByRole('button',{name:'Продолжить',exact:true}).click();
  await page.waitForTimeout(50);assert.ok(await page.evaluate(()=>oniStudy.time<450),'resume from end restarts');
  await page.evaluate(()=>oniStudy.seek(1920));
  await page.locator('#skeleton').check();await page.locator('#skeleton').uncheck();
  await page.screenshot({path:path.join(__dirname,'studies/preview-dark.png'),fullPage:true});
  await page.getByRole('button',{name:'Светлый фон'}).click();
  await page.screenshot({path:path.join(__dirname,'studies/preview-light.png'),fullPage:true});
  await page.setViewportSize({width:390,height:844});
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'no mobile overflow');
  await page.screenshot({path:path.join(__dirname,'studies/preview-mobile.png'),fullPage:true});
  assert.deepEqual(errors,[]);
  console.log('PASS: 12 poses, pause, replay, 0.25x, endpoint, light/dark, mobile overflow, no runtime errors.');
 }finally{await browser.close()}
})().catch(e=>{console.error(e);process.exitCode=1});
