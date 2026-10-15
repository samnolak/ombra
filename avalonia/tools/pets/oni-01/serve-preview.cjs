const http=require('node:http'),fs=require('node:fs'),path=require('node:path');
const {saveExport,maxBytes}=require('./export-store.cjs');
const {serveFile}=require('./serve-file.cjs');
const directory=path.join(__dirname,'exports'),port=Number(process.env.ONI_PREVIEW_PORT||4186);
const states=require('./frames/approved-v1/manifest.json'),assets=require('./transitions/manifest.json');
const imagePaths=new Set([...states.states.map(s=>'/'+s.source),...Object.values(assets).map(a=>'/'+a.source)]);
http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://127.0.0.1:'+port),name=url.pathname.slice('/exports/'.length);
 res.setHeader('Cache-Control','no-store');
 if(req.method==='GET'&&imagePaths.has(url.pathname)){const file=path.join(__dirname,url.pathname.slice(1));if(!fs.existsSync(file)){res.writeHead(404);res.end();return}serveFile(req,res,file,'image/png');return}
 const pages={'/':'preview.html','/review':'studies/playback-review.html','/joy-study':'studies/joy-torso-study.html','/appearance':'approved/character-states.png'};
 if(req.method==='GET'&&pages[url.pathname]){if(!fs.existsSync(path.join(__dirname,pages[url.pathname]))){res.writeHead(404);res.end('Preview page not packaged');return}res.setHeader('Content-Type',pages[url.pathname].endsWith('.png')?'image/png':'text/html; charset=utf-8');res.end(fs.readFileSync(path.join(__dirname,pages[url.pathname])));return}
 if(url.pathname.startsWith('/exports/')&&req.method==='POST'){
  if(req.headers.origin!=='http://127.0.0.1:'+port&&req.headers.origin!=='http://localhost:'+port){res.writeHead(403);res.end('Same-origin export only');return}
  try{let size=0;const chunks=[];for await(const chunk of req){size+=chunk.length;if(size>maxBytes)throw Error('Export too large');chunks.push(chunk)}
   const result=saveExport(directory,name,Buffer.concat(chunks));res.setHeader('Content-Type','application/json');res.end(JSON.stringify({...result,path:'exports/'+result.file,url:'/exports/'+result.file}));
  }catch(error){res.writeHead(400);res.end(error.message)}return;
 }
 if(req.method==='GET'&&/^oni-[a-z0-9-]+\.(png|webm)$/.test(name)&&url.pathname.startsWith('/exports/')){
  const file=path.join(directory,name);if(fs.existsSync(file)){serveFile(req,res,file,name.endsWith('.png')?'image/png':'video/webm');return}
 }
 res.writeHead(404);res.end();
}).listen(port,'127.0.0.1',()=>console.log('ONI-01: http://127.0.0.1:'+port+'/ — exports saved inside project'));
