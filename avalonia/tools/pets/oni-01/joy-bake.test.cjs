const {test}=require('node:test'),a=require('node:assert/strict'),path=require('node:path');
test('baked joy keeps the lower legs identical across all 64 drawings while the torso moves',async()=>{
 const sharp=require('sharp');const {data,info}=await sharp(path.join(__dirname,'frames/approved-v1/joy-torso-v11.png')).ensureAlpha().raw().toBuffer({resolveWithObject:true});
 a.equal(info.width,2048);a.equal(info.height,2560);let upperDifferent=0;
 for(let frame=1;frame<64;frame++)for(let y=0;y<320;y++)for(let x=0;x<256;x++){
  const first=(y*info.width+x)*4,other=((Math.floor(frame/8)*320+y)*info.width+frame%8*256+x)*4;
  for(let c=0;c<4;c++){if(y>=240)a.equal(data[first+c],data[other+c],`leg changed in frame ${frame}`);else if(data[first+c]!==data[other+c])upperDifferent++;}
 }
 a.ok(upperDifferent>10000,'torso must visibly move, not freeze');
});
