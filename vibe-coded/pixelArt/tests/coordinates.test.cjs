const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const path = require('node:path');

(async () => {
  const browser = await chromium.launch({headless:true});
  const page = await browser.newPage({viewport:{width:1440,height:1000},deviceScaleFactor:1.5});
  const errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  try {
    await page.goto('file://'+path.resolve('index.html'));
    await page.locator('[data-tool="brush"]').click();
    for (const configuration of [
      {zoom:7,panX:45,panY:85,width:64,height:64,scale:1,tile:false},
      {zoom:4.35,panX:103.25,panY:151.5,width:73,height:41,scale:1,tile:false},
      {zoom:6.25,panX:49.5,panY:90.75,width:64,height:64,scale:.8,tile:false},
      {zoom:5.75,panX:60.25,panY:95.5,width:64,height:64,scale:1,cssZoom:1.2,tile:false},
      {zoom:2.25,panX:220.5,panY:240.25,width:64,height:64,scale:.8,tile:true}
    ]) {
      await page.evaluate(c=>{
        document.body.style.zoom=c.cssZoom || 1;
        document.getElementById('app').style.transform=`scale(${c.scale})`;
        document.getElementById('app').style.transformOrigin='top left';
        app.layers=[];app.canvasWidth=c.width;app.canvasHeight=c.height;addLayer('Coordinates');
        app.tileView=c.tile;app.zoom=c.zoom;app.panX=c.panX;app.panY=c.panY;
        app.brushSize=1;app.color1={r:123,g:45,b:67,a:255};updateCanvas();
      },configuration);
      const {width,height,tile}=configuration;
      const targets=[[0,0],[width-1,0],[0,height-1],[width-1,height-1],[17,23]];
      if(tile) targets.push([-1,20],[width,21],[15,-1],[16,height]);
      for(const [x,y] of targets) {
        await page.evaluate(()=>getActiveLayer().data.fill(0));
        // Derive pointer positions from the displayed canvas, independently of app pan/zoom.
        const point=await page.locator('#main-canvas').evaluate((canvas,{x,y})=>{
          const r=canvas.getBoundingClientRect();
          return {x:r.left+(x+.5)*r.width/canvas.width,y:r.top+(y+.5)*r.height/canvas.height};
        },{x,y});
        await page.mouse.click(point.x,point.y);
        const painted=await page.evaluate(()=>{
          const result=[],data=getActiveLayer().data;
          for(let i=0;i<data.length;i+=4) if(data[i+3]) result.push([i/4%app.canvasWidth,Math.floor(i/4/app.canvasWidth)]);
          return result;
        });
        assert.deepEqual(painted,[[((x%width)+width)%width,((y%height)+height)%height]],`Clicked ${x},${y} in ${JSON.stringify(configuration)}`);
        await page.keyboard.down('Alt');await page.mouse.click(point.x,point.y);await page.keyboard.up('Alt');
        assert.deepEqual(await page.evaluate(()=>app.color1),{r:123,g:45,b:67,a:255});
      }
    }
    assert.deepEqual(errors,[]);
    console.log('PASS: displayed pixel hitboxes match paint and sampling at canvas edges, fractional zoom, pan, display scaling, and tile seams.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
