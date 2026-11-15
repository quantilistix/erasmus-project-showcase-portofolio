const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const path=require('node:path');
(async()=>{
  const browser=await chromium.launch();
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000},deviceScaleFactor:1.25});
    await page.goto('file://'+path.resolve('index.html'));
    await page.locator('[data-tool="brush"]').click();
    await page.evaluate(()=>{
      app.layers=[];app.canvasWidth=8;app.canvasHeight=8;addLayer('Eight pixels');
      app.zoom=63.75;app.panX=61.25;app.panY=130.5;app.tileView=false;app.brushSize=1;
      app.color1={r:37,g:83,b:181,a:255};updateCanvas();renderViewport();
    });
    // Test the beginning and end of every pixel's area, not just its center.
    // A half-artwork-pixel scale drift is especially obvious in the final row
    // and column: their lower/right portions would hit the adjacent pixel.
    for(let y=0;y<8;y++) for(let x=0;x<8;x++) {
      for(const [fx,fy] of [[.03,.03],[.97,.03],[.5,.5],[.03,.97],[.97,.97]]) {
        await page.evaluate(()=>getActiveLayer().data.fill(0));
        const point=await page.evaluate(({x,y,fx,fy})=>{
          const r=artworkViewBounds();return{x:r.left+(x+fx)*r.width/8,y:r.top+(y+fy)*r.height/8};
        },{x,y,fx,fy});
        await page.mouse.click(point.x,point.y);
        const marked=await page.evaluate(()=>{
          const points=[],data=getActiveLayer().data;
          for(let i=0;i<data.length;i+=4) if(data[i+3])points.push([i/4%8,Math.floor(i/4/8)]);
          return points;
        });
        assert.deepEqual(marked,[[x,y]],`8×8 pixel ${x},${y}, position ${fx},${fy}`);
      }
    }
    // Drag across the full small artwork as well, ending inside the last cell.
    await page.evaluate(()=>getActiveLayer().data.fill(0));
    const row=await page.evaluate(()=>{
      const r=artworkViewBounds();return{left:r.left+r.width*.01,right:r.right-r.width*.01,y:r.top+r.height*7.75/8};
    });
    await page.mouse.move(row.left,row.y);await page.mouse.down();
    await page.mouse.move(row.right,row.y,{steps:64});await page.mouse.up();
    assert.equal(await page.evaluate(()=>{
      const d=getActiveLayer().data;
      return Array.from({length:8},(_,x)=>d[(7*8+x)*4+3]).every(a=>a===255);
    }),true);
    console.log('PASS: every part of all 64 pixels in an 8×8 artwork paints the intended cell, including the far edges and a full-width stroke.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
