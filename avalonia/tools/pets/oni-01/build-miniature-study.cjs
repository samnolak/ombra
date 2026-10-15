// Static layout only: display the generated artwork without changing its pixels.
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');
(async () => {
 const filename=path.join(__dirname,'approved/character-states.png');
 const {data,info}=await sharp(filename).ensureAlpha().raw().toBuffer({resolveWithObject:true});
 const bounds=[];
 for(let i=0;i<8;i++){
  const sx=i%4*info.width/4;
  // The generated first-column gutter sits 3px above the nominal half.
  const split=i%4===0?509:info.height/2, sy=i<4?0:split, ey=i<4?split:info.height;
  let l=info.width,t=info.height,r=0,b=0;
  for(let y=sy;y<ey;y++) for(let x=sx;x<sx+info.width/4;x++){
   if(data[(y*info.width+x)*4+3]>32){l=Math.min(l,x);r=Math.max(r,x);t=Math.min(t,y);b=Math.max(b,y);}
  }
  bounds.push({x:l,y:t,w:r-l+1,h:b-t+1});
 }
 const names=['Обычный','Радость','Работа','Размышление','Вопрос / ожидание','Сон','Ошибка','Перетаскивание'];
 const uri='data:image/png;base64,'+fs.readFileSync(filename).toString('base64');
 const html=`<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>ONI-01 · Миниатюры и эмоции</title>
 <style>*{box-sizing:border-box}body{margin:0;background:#201e26;color:#eeeaf3;font:14px system-ui}main{max-width:1000px;margin:auto;padding:24px}h1{font-size:25px;margin:0 0 8px}p{color:#bbb5c7;line-height:1.5;margin:8px 0 16px}button{background:#393340;color:inherit;border:1px solid #65566e;border-radius:8px;padding:8px 14px;cursor:pointer}.row{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));align-items:end;border-top:1px solid #4b4352;padding:14px 0;gap:8px}.size{grid-column:1/-1;align-self:center;font-weight:600;color:#c6b3cc}figure{margin:0;text-align:center}canvas{display:block;margin:0 auto}figcaption{font-size:12px;white-space:nowrap;margin-top:8px;color:#d3cbdc}body.light{background:#ece8e3;color:#26212b}.light p,.light figcaption,.light .size{color:#594c62}.light button{background:#ded5e4;color:#26212b}.note{font-size:12px} .row[hidden]{display:none}.controls{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:12px}button.active{border-color:#ff4c70;background:#643047;color:white}</style>
 <main><h1>ONI-01 · Контур, простые формы и эмоции</h1><p>Утверждённый облик ONI-01. Маска, светлое ядро глаз, красный ореол и отсвет на металле.<br>Размер — высота самого высокого персонажа в ряду; сидящие позы сохраняют общий масштаб.</p><div class="controls"><button id="theme">Светлый / тёмный фон</button><button data-size="64">64 px</button><button data-size="80">80 px</button><button data-size="96">96 px</button><button data-size="128" class="active">128 px</button></div><div id="rows"></div><p class="note">Облик утверждён пользователем. На исходном листе сон показан предварительно; в новом анимационном проходе глаза закрыты. <a href="http://127.0.0.1:4186/" style="color:#ff8096">Открыть восемь обновлённых анимаций →</a> В Ombra не внедрено.</p></main>
 <script>const bounds=${JSON.stringify(bounds)},names=${JSON.stringify(names)},img=new Image();img.src=${JSON.stringify(uri)};img.onload=()=>{const mh=Math.max(...bounds.map(b=>b.h));for(const size of [128,96,80,64]){const row=document.createElement('section');row.className='row';row.dataset.size=size;row.hidden=size!==128;row.innerHTML='<div class="size">'+size+' px</div>';bounds.forEach((b,i)=>{const figure=document.createElement('figure'),c=document.createElement('canvas'),s=size/mh,d=devicePixelRatio||1;c.style.width='140px';c.style.height=size+'px';c.width=140*d;c.height=size*d;const ctx=c.getContext('2d');ctx.scale(d,d);ctx.imageSmoothingQuality='high';ctx.drawImage(img,b.x,b.y,b.w,b.h,(140-b.w*s)/2,size-b.h*s,b.w*s,b.h*s);figure.append(c);const label=document.createElement('figcaption');label.textContent=names[i];figure.append(label);row.append(figure)});document.querySelector('#rows').append(row)}};document.querySelector('#theme').onclick=()=>document.body.classList.toggle('light');document.querySelectorAll('button[data-size]').forEach(button=>button.onclick=()=>{document.querySelectorAll('.row').forEach(row=>row.hidden=row.dataset.size!==button.dataset.size);document.querySelectorAll('button[data-size]').forEach(b=>b.classList.toggle('active',b===button))});</script></html>`;
 fs.writeFileSync(path.join(__dirname,'studies/miniature-style-v3.html'),html);
 // Compatibility entry used by the already-open localhost preview.
 fs.writeFileSync(path.join(__dirname,'studies/miniature-style-v1.html'),html);
 fs.writeFileSync(path.join(__dirname,'studies/miniature-style-v3.bounds.json'),JSON.stringify(bounds,null,2)+'\n');
 console.log(JSON.stringify(bounds));
})();
