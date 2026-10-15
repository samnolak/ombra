const fs=require('node:fs');
function serveFile(req,res,file,type){
 const size=fs.statSync(file).size;let start=0,end=size-1;
 res.setHeader('Content-Type',type);res.setHeader('Accept-Ranges','bytes');
 const range=req.headers.range;
 if(range){
  const m=/^bytes=(\d*)-(\d*)$/.exec(range);
  if(m&&(m[1]||m[2])){start=m[1]?Number(m[1]):Math.max(0,size-Number(m[2]));end=m[1]&&m[2]?Math.min(Number(m[2]),size-1):size-1;}
  if(!m||!(m[1]||m[2])||!Number.isSafeInteger(start)||!Number.isSafeInteger(end)||start>end||start>=size){res.setHeader('Content-Range','bytes */'+size);res.writeHead(416);res.end();return}
  res.setHeader('Content-Range',`bytes ${start}-${end}/${size}`);
 }
 res.setHeader('Content-Length',end-start+1);if(range)res.writeHead(206);fs.createReadStream(file,{start,end}).pipe(res);
}
module.exports={serveFile};
