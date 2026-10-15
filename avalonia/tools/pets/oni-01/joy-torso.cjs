(function(root){
 const duration=2800,count=64;
 // Two rigid painted masses at the waist. This is a bake-only authoring
 // operation: the delivery player consumes whole frames, no skeletal rig.
 const source="transitions/joy-entry.png",sourceFrame=7;
 const geometry={width:362,height:362,sx:1086,sy:362,waistX:174,waistY:242,groundY:355,cutY:244,overlap:4};
 function drawing(ctx,image){const g=geometry;ctx.drawImage(image,g.sx,g.sy,g.width,g.height,0,0,g.width,g.height)}
 function angle(time){return Math.sin(time/duration*Math.PI*2)*.065;}
 function draw(ctx,image,time,x,baseline,scale){
  const g=geometry;ctx.save();ctx.translate(x,baseline);ctx.scale(scale,scale);ctx.translate(-g.waistX,-g.groundY);
  ctx.save();ctx.beginPath();ctx.rect(0,g.cutY,g.width,g.height-g.cutY);ctx.clip();drawing(ctx,image);ctx.restore();
  ctx.translate(g.waistX,g.waistY);ctx.rotate(angle(time));ctx.translate(-g.waistX,-g.waistY);
  ctx.beginPath();ctx.rect(0,0,g.width,g.cutY+g.overlap);ctx.clip();drawing(ctx,image);ctx.restore();
 }
 const api={duration,count,source,sourceFrame,geometry,angle,draw};if(typeof module==='object')module.exports=api;else root.OniJoy=api;
})(typeof window==='object'?window:globalThis);
