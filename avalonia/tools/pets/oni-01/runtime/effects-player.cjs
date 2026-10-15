(function(root){
 const indicators={idle:'',joy:'',working:'',thinking:'dots',waiting:'?',sleeping:'z',error:'!!',dragging:''};
 // Both spirits persist; their energy supports each state's main cue.
 const policy={idle:['spirits'],joy:['hopping-spirits'],working:['seal-orbit-spirits','rune'],thinking:['pondering-spirits','dots'],waiting:['pulsing-spirits','?'],sleeping:['hiding-spirits','z'],error:['panicking-spirits','!!','recurring-glitch'],dragging:['crossed-orbit-spirits']};
 function spiritPose(id,t,i){
  const side=i?1:-1,p=t*(i?1.47:1.8)+i*2.2;
  let x=side*.43+Math.sin(p*.63)*.025,y=-.43+(i?.06:-.06)+Math.sin(p)*.025,depth=-1,scale=.21,alpha=i?.72:.85,tempo=1,lean=Math.sin(p*.8)*.055;
  if(id==='joy'){
   const u=(t*.7+i*.36)%1,hop=u<.72?4*(u/.72)*(1-u/.72):0;
   x=side*.48+Math.sin(p)*.015;y=-.38-hop*.08;scale=.22*(1+hop*.08-(u>.8?.08*Math.sin((u-.8)/.2*Math.PI):0));alpha=.95;tempo=1.5;
  }else if(id==='working'){
   const a=t*1.1+i*Math.PI;x=Math.cos(a)*.49;y=-.43+Math.sin(a)*.43;scale=.18;tempo=1.2;alpha=.85;
  }else if(id==='thinking'){
   const a=t*.55+i*Math.PI;x=Math.cos(a)*.49;y=-.43+Math.sin(a)*.1;depth=Math.sin(a);scale=.18*(1+depth*.1);tempo=.65;
  }else if(id==='waiting'){
   const pulse=(1+Math.sin(t*3+i*Math.PI))/2;x=side*.49;y=-.42-pulse*.035;scale=.20*(1+pulse*.25);alpha=.72+pulse*.23;tempo=.9;
  }else if(id==='sleeping'){
   const u=Math.min(1,t/1.8),ease=u*u*(3-2*u);x=side*(.43-.32*ease);y=-.3-ease*.03;scale=.17;alpha=.6*(1-ease);tempo=.35;
  }else if(id==='error'){
   const a=t*3.4+i*Math.PI;x=Math.sin(a)*.51+Math.sin(t*7.1+i)*.05;y=-.46+Math.sin(t*4.9+i*2.5)*.17;
   depth=y>-.37?1:-1;scale=.20;alpha=.95;tempo=1.8;lean=Math.cos(a)*.16;
  }else if(id==='dragging'){
   const o=OniInteraction.orbit(t,i,1);x=o.x;y=-.47+o.y;depth=o.depth;scale=.2*(1+depth*.13);alpha=.82+depth*.1;tempo=1.7;
  }
  return {x,y,depth,scale,alpha,tempo,lean};
 }
 function create(asset,flames,vfx){
  let lightTheme=false;
  const im=new Image();im.src=asset.image;const fire=new Image();fire.src=flames.image;const shots=new Image();shots.src=vfx.image;
  function sprite(ctx,n,x,y,w,alpha=1,rotation=0){const f=asset.frames[n],h=w*f.ch/f.cw;ctx.save();ctx.globalAlpha=lightTheme?Math.min(1,alpha*1.3):alpha;if(lightTheme)ctx.filter='brightness(.65) saturate(1.8)';ctx.translate(x,y);ctx.rotate(rotation);ctx.drawImage(im,f.sx,f.sy,f.cw,f.ch,-w/2,-h/2,w,h);ctx.restore()}
  function spirit(ctx,color,t,x,y,height,alpha=1,lean=0,still=false){
   const phrase=(t+color*2.1)%6.7,blink=!still&&phrase>4.3&&phrase<4.53;
   const open=color?[0,3,4,5,6,7,6,5,4,3]:[0,1,2,3,6,7,6,3,2,1];
   const index=still?0:blink?(color?1:4)+(phrase>4.42?1:0):open[Math.floor(t*(color?7:8))%open.length];
   const f=flames.frames[color*8+index],s=height/280;
   ctx.save();ctx.globalAlpha=alpha;ctx.shadowColor=lightTheme?(color?'#602078':'#9b1238'):(color?'#b66aff':'#ff285a');ctx.shadowBlur=lightTheme?Math.max(1,height*.035):height*.18;if(lightTheme)ctx.filter='saturate(1.6) contrast(1.12)';ctx.translate(x,y);ctx.rotate(lean);ctx.drawImage(fire,f.sx,f.sy,f.cw,f.ch,-f.ax*s,-(f.ay-125)*s,f.cw*s,f.ch*s);ctx.restore();
  }
  function event(ctx,row,age,x,y,h,duration=.65,angle=0){
   if(age<0||age>=duration)return;const u=age/duration,f=vfx.frames[row*4+Math.min(3,Math.floor(u*4))],scale=h/Math.max(...vfx.frames.slice(row*4,row*4+4).map(q=>Math.max(q.b-q.t+1,q.r-q.l+1)));
   ctx.save();ctx.translate(x,y);ctx.rotate(angle);ctx.globalAlpha=Math.min(1,u*12)*(1-Math.pow(u,3));if(lightTheme)ctx.filter='brightness(.7) saturate(1.7)';ctx.drawImage(shots,f.sx,f.sy,f.cw,f.ch,-(f.l+f.r)/2*scale,-(f.t+f.b)/2*scale,f.cw*scale,f.ch*scale);ctx.restore();
  }
  function draw(ctx,id,t,x,y,h,front=false,staticOnly=false,withSpirits=true){
   const s=staticOnly?(id==='sleeping'?3:0):t/1000,bob=Math.sin(s*1.3)*h*.02;
   function companions(){if(!withSpirits)return;for(let i=0;i<2;i++){
    const p=spiritPose(id,s,i);if((p.depth>0)!==front||p.alpha<.01)continue;
    const px=x+p.x*h,py=y+p.y*h;
    spirit(ctx,i,s*p.tempo,px,py,h*p.scale,p.alpha,staticOnly?0:p.lean,staticOnly);
    if(id==='thinking'){ctx.save();ctx.fillStyle='#eac8f0';ctx.globalAlpha=.6;for(let j=0;j<2;j++){ctx.beginPath();ctx.arc(px+h*(j*.045-.02),py-h*(.14+j*.02),Math.max(1,h*.009),0,Math.PI*2);ctx.fill()}ctx.restore()}
   }}
   if(id==='dragging'){companions();return}
   if(!front){
    if(id==='working'){
     const rx=x,ry=y-h*.43,r=h*.47,pulse=.9+Math.sin(s*1.5)*.1;
     // Full seal behind the silhouette. Independent rings/light packets;
     // no wedge clipping of the source artwork.
     sprite(ctx,3,rx,ry,h*1.08,.64*pulse,staticOnly?0:s*.12);
     ctx.save();ctx.translate(rx,ry);ctx.shadowColor='#65d9ef';ctx.shadowBlur=lightTheme?0:h*.055;ctx.lineWidth=Math.max(1,h*.006);ctx.strokeStyle=lightTheme?'#14628c':'#58cadbaa';
     ctx.beginPath();ctx.arc(0,0,r,0,Math.PI*2);ctx.stroke();
     for(let j=0;j<3;j++){const a=(staticOnly?0:s*.4)+j*Math.PI*2/3;ctx.strokeStyle=lightTheme?(j===1?'#863c97':'#14628c'):(j===1?'#ce75de99':'#78edffbb');ctx.beginPath();ctx.arc(0,0,r*.84,a,a+.42);ctx.stroke();const px=Math.cos(-a*.8)*r,py=Math.sin(-a*.8)*r;ctx.fillStyle=lightTheme?'#14628c':'#adeaff';ctx.beginPath();ctx.arc(px,py,Math.max(1,h*.012),0,Math.PI*2);ctx.fill()}
     for(let j=0;j<8;j++){const a=j*Math.PI/4-(staticOnly?0:s*.08),rr=r*.94,g=h*.025;ctx.save();ctx.rotate(a);ctx.translate(rr,0);ctx.strokeStyle=lightTheme?(j%2?'#863c97':'#14628c'):(j%2?'#c891f2c0':'#7ce9f5d0');ctx.lineWidth=Math.max(1,h*.005);ctx.beginPath();ctx.moveTo(-g,0);ctx.lineTo(0,-g*.7);ctx.lineTo(g,0);ctx.lineTo(0,g*.7);ctx.closePath();ctx.moveTo(g*1.4,0);ctx.lineTo(g*2,0);ctx.stroke();ctx.restore()}
     ctx.restore();
    }
    // Wide centered interference stays BEHIND the body, visible on both sides.
    if(id==='error'&&!staticOnly)event(ctx,2,s%2.1,x,y-h*.47,h*1.3,1.05);
    companions();
    return;
   }
   companions();
   const token=indicators[id];ctx.save();ctx.fillStyle=lightTheme?'#ad123c':'#ff607e';ctx.shadowColor=lightTheme?'#ffffff':'#ff164b';ctx.shadowBlur=lightTheme?0:h*.04;ctx.textAlign='center';ctx.textBaseline='middle';ctx.font='bold '+Math.max(13,h*.17)+'px system-ui';
   const tx=x+h*.38,ty=y-h*.92;
   if(token==='dots'){for(let j=0;j<3;j++){const lift=staticOnly?0:Math.max(0,Math.sin(s*4-j*.8))*h*.045;ctx.beginPath();ctx.arc(x+(j-1)*h*.085,y-h*1.06-lift,Math.max(2,h*.023),0,Math.PI*2);ctx.fill()}}
   else if(token==='z'){
    const newest=Math.floor(s/.9);
    for(let j=0;j<4;j++){
     const age=staticOnly?.35+j*.8:s-(newest-j)*.9;if((!staticOnly&&newest-j<0)||age<0||age>=2.8)continue;
     const u=age/2.8,alpha=Math.min(1,age/.22)*Math.min(1,(2.8-age)/.7);
     ctx.globalAlpha=alpha*.9;ctx.fillStyle=lightTheme?'#77348d':'#d1a0e8';ctx.font='bold '+Math.max(14,h*(.09+.12*u))+'px system-ui';
     ctx.fillText(u<.3?'z':'Z',x+h*(.28+.22*u)+Math.sin(u*3)*h*.025,y-h*(.7+.43*u));
    }
   }
   else if(token)ctx.fillText(token,tx,ty+(id==='waiting'?bob:0));ctx.restore();
  }
  return {draw,sprite,spirit,event,setTheme:value=>{lightTheme=value==='light'},ready:Promise.all([im.decode(),fire.decode(),shots.decode()])};
 }
 const api={indicators,policy,spiritPose,create};if(typeof module==='object')module.exports=api;else root.OniEffects=api;
})(typeof window==='object'?window:globalThis);
