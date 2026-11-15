const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const path=require('node:path');
(async()=>{
  const browser=await chromium.launch();
  try {
    for(const dpr of [1,1.5,2]) {
      const page=await browser.newPage({viewport:{width:1440,height:1000},deviceScaleFactor:dpr});
      await page.goto('file://'+path.resolve('index.html'));
      for(const {size,zoom} of [{size:8,zoom:32},{size:8,zoom:63.75},{size:16,zoom:31.75},...([3,4.35,7.5,13].map(zoom=>({size:64,zoom})))]) {
        await page.evaluate(({size,zoom})=>{
          app.layers=[];app.canvasWidth=size;app.canvasHeight=size;addLayer('Pixel boundaries');
          app.zoom=zoom;app.panX=60.25;app.panY=100.25;
          for(let y=0;y<size;y++) for(let x=0;x<size;x++) {
            // Each pixel has a unique opaque color, so screenshot colors tell
            // us which source pixel the browser actually rendered at an edge.
            getActiveLayer().data.set([10+x*3,10+y*3,81,255],(y*size+x)*4);
          }
          updateCanvas();renderViewport();
        },{size,zoom});
        const screenshot=(await page.screenshot()).toString('base64');
        const result=await page.evaluate(async({screenshot,dpr})=>{
          const img=new Image();img.src='data:image/png;base64,'+screenshot;await img.decode();
          const canvas=document.createElement('canvas');canvas.width=img.width;canvas.height=img.height;
          const ctx=canvas.getContext('2d');ctx.drawImage(img,0,0);
          const data=ctx.getImageData(0,0,canvas.width,canvas.height).data;
          const rect=mainCanvas.getBoundingClientRect();
          let checked=0,wrong=0,blended=0;
          for(let sy=Math.ceil((rect.top+1)*dpr);sy<Math.min(canvas.height,Math.floor((rect.bottom-1)*dpr));sy++) {
            for(let sx=Math.ceil((rect.left+1)*dpr);sx<Math.min(canvas.width,Math.floor((rect.right-1)*dpr));sx++) {
              const i=(sy*canvas.width+sx)*4;
              if(data[i+2]!==81) continue; // Ignore toolbar/status outside the artwork.
              const actual={x:(data[i]-10)/3,y:(data[i+1]-10)/3};
              if(!Number.isInteger(actual.x)||!Number.isInteger(actual.y)) {blended++;continue;}
              const expected=getCanvasCoords({clientX:(sx+.5)/dpr,clientY:(sy+.5)/dpr});
              if(actual.x!==expected.x||actual.y!==expected.y) wrong++;
              checked++;
            }
          }
          return {checked,wrong,blended};
        },{screenshot,dpr});
        assert.ok(result.checked>10000);
        assert.equal(result.wrong,0,`Rendered boundaries vs input: ${size}×${size}, DPR ${dpr}, zoom ${zoom}`);
        assert.equal(result.blended,0,`Unblended pixel colors: ${size}×${size}, DPR ${dpr}, zoom ${zoom}`);
      }
      await page.close();
    }
    console.log('PASS: actual screenshot pixel boundaries match hitboxes at 100%, 150%, and 200% display scaling, including fractional zoom and 8×8 / 16×16 artwork.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
