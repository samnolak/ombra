const {test}=require('node:test'),a=require('node:assert/strict');
const {PointerGesture}=require('./interaction.cjs');
test('small pointer jitter is a click and never a drag',()=>{
 a.equal(typeof PointerGesture,'function');const g=new PointerGesture(6);
 g.down(1,100,100);a.equal(g.move(1,103,102),'pending');a.equal(g.up(1),'click');a.equal(g.up(1),'none');
});
test('threshold crossing starts drag once; unrelated pointers cannot release it',()=>{
 a.equal(typeof PointerGesture,'function');const g=new PointerGesture(6);
 g.down(2,0,0);a.equal(g.move(1,50,0),'none');a.equal(g.move(2,7,0),'start');a.equal(g.move(2,20,0),'drag');a.equal(g.up(1),'none');a.equal(g.up(2),'drop');
});
test('capture cancellation drops a drag but never produces a click',()=>{
 a.equal(typeof PointerGesture,'function');const g=new PointerGesture();g.down(1,0,0);a.equal(g.up(1,true),'cancel');g.down(1,0,0);g.move(1,20,0);a.equal(g.up(1,true),'drop');a.equal(g.up(1),'none');
});
