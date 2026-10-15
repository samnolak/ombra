(function(root){
 function timeline(frames,step,hold,apex){
  if(frames.length<2||[step,hold,apex].some(n=>!Number.isFinite(n)||n<=0))throw Error('Invalid clip');
  const sequence=[...frames,...frames.slice(1,-1).reverse()];
  let total=0;
  const keys=sequence.map((frame,i)=>{const duration=i===0?hold:i===frames.length-1?apex:step;const key={frame,start:total,duration};total+=duration;return key});
  return {keys,duration:total};
 }
 function sample(clip,time,loop=true){
  let t=loop?((time%clip.duration)+clip.duration)%clip.duration:Math.max(0,Math.min(time,clip.duration-0.001));
  for(let i=clip.keys.length-1;i>=0;i--)if(t>=clip.keys[i].start)return clip.keys[i];
  return clip.keys[0];
 }
 const api={timeline,sample};if(typeof module==='object')module.exports=api;else root.OniFrames=api;
})(typeof window==='object'?window:globalThis);
