const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const path=require('node:path');
const fs=require('node:fs/promises');
(async()=>{
  const browser=await chromium.launch();
  const page=await browser.newPage({viewport:{width:1440,height:1000},acceptDownloads:true});
  const errors=[];page.on('pageerror',error=>errors.push(error.message));
  try {
    await page.goto('file://'+path.resolve('index.html'));
    const layer=()=>page.locator('.layer-item').first();
    const action=(name)=>layer().locator(`[data-layer-action="${name}"]`);
    const point=()=>page.evaluate(()=>{const r=artworkViewBounds();return{x:r.left+r.width*.5,y:r.top+r.height*.5};});
    await page.locator('[data-tool="brush"]').click();
    // Real mouse clicks, not onclick invocations: mouseup must retain the target.
    await action('lock').click();
    assert.equal(await page.evaluate(()=>getActiveLayer().locked),true);
    await page.mouse.click(...Object.values(await point()));
    assert.equal(await page.evaluate(()=>getActiveLayer().data.some(n=>n!==0)),false);
    await action('lock').click();
    assert.equal(await page.evaluate(()=>getActiveLayer().locked),false);
    await page.mouse.click(...Object.values(await point()));
    assert.equal(await page.evaluate(()=>getActiveLayer().data.some(n=>n!==0)),true);
    await action('visibility').click();
    assert.equal(await page.evaluate(()=>getActiveLayer().visible),false);
    assert.equal(await layer().isVisible(),true);
    await action('visibility').click();
    assert.equal(await page.evaluate(()=>getActiveLayer().visible),true);
    // Clicking the selected layer again deselects; no painting without a layer.
    await layer().locator('.layer-name').click();
    assert.equal(await page.evaluate(()=>app.activeLayerId),null);
    const previous=await page.evaluate(()=>Array.from(app.layers[0].data));
    await page.mouse.click(...Object.values(await point()));
    assert.deepEqual(await page.evaluate(()=>Array.from(app.layers[0].data)),previous);
    await layer().locator('.layer-name').click();
    assert.ok(await page.evaluate(()=>app.activeLayerId));
    // Stable rows also preserve the browser's double-click sequence.
    await layer().locator('.layer-name').dblclick();
    await page.locator('#layer-new-name').fill('My layer');
    await page.locator('#dialog-ok').click();
    assert.equal(await page.evaluate(()=>getActiveLayer().name),'My layer');
    await page.locator('#add-layer-btn').click();
    assert.equal(await page.locator('.layer-item').count(),2);
    await action('delete').click();
    assert.equal(await page.locator('.layer-item').count(),1);
    await page.locator('#undo-btn').click();
    assert.equal(await page.locator('.layer-item').count(),2);
    await action('delete').click();
    await action('delete').click();
    assert.equal(await page.locator('.layer-item').count(),0);
    assert.equal(await page.evaluate(()=>app.activeLayerId),null);
    await page.locator('#add-layer-btn').click();
    assert.equal(await page.locator('.layer-item').count(),1);

    // New Image takes exact dimensions, even smaller than the old 8px minimum.
    await page.locator('#new-image-btn').click();
    await page.locator('#new-w').fill('0');await page.locator('#dialog-ok').click();
    assert.equal(await page.locator('#new-w').isVisible(),true);
    await page.locator('#new-w').fill('4');await page.locator('#new-h').fill('7');
    await page.locator('#dialog-ok').click();
    assert.deepEqual(await page.evaluate(()=>[app.canvasWidth,app.canvasHeight,app.layers.length,getActiveLayer().data.length]),[4,7,1,4*7*4]);
    const originalId=await page.evaluate(()=>{paintPixel(1,1,{r:231,g:21,b:89,a:255});render();return getActiveLayer().id;});
    const originalBytes=await page.evaluate(()=>Array.from(getActiveLayer().data));
    async function importPattern(width,height,name) {
      const base64=await page.evaluate(({width,height})=>{
        const canvas=document.createElement('canvas');canvas.width=width;canvas.height=height;
        const ctx=canvas.getContext('2d');
        for(let y=0;y<height;y++)for(let x=0;x<width;x++) {
          ctx.fillStyle=`rgb(${x%256},${y%256},91)`;ctx.fillRect(x,y,1,1);
        }
        return canvas.toDataURL().split(',')[1];
      },{width,height});
      const count=await page.locator('.layer-item').count();
      await page.locator('#file-input').setInputFiles({name,mimeType:'image/png',buffer:Buffer.from(base64,'base64')});
      await page.waitForFunction(count=>app.layers.length===count+1,count);
    }
    await importPattern(9,11,'larger.png');
    assert.deepEqual(await page.evaluate(()=>[app.canvasWidth,app.canvasHeight,app.layers.length]),[4,7,2]);
    assert.deepEqual(await page.evaluate(()=>Array.from(app.layers[1].data)),originalBytes);
    assert.equal(await page.evaluate(()=>app.layers[1].id),originalId);
    assert.deepEqual(await page.evaluate(()=>Array.from(getActiveLayer().data.slice((6*4+3)*4,(6*4+3)*4+4))),[3,6,91,255]);
    await page.locator('#undo-btn').click();assert.equal(await page.locator('.layer-item').count(),1);
    await page.locator('#redo-btn').click();assert.equal(await page.locator('.layer-item').count(),2);
    await importPattern(2,3,'smaller.png');
    assert.deepEqual(await page.evaluate(()=>Array.from(getActiveLayer().data.slice((2*4+1)*4,(2*4+1)*4+4))),[1,2,91,255]);
    assert.equal(await page.evaluate(()=>getActiveLayer().data[(2*4+2)*4+3]),0);
    assert.equal(await page.evaluate(()=>getActiveLayer().data[(3*4+1)*4+3]),0);
    // Re-importing the same file still adds another layer.
    await importPattern(2,3,'smaller.png');
    assert.equal(await page.locator('.layer-item').count(),4);
    await page.locator('.layer-item').first().locator('.layer-name').click();
    assert.equal(await page.evaluate(()=>app.activeLayerId),null);
    await page.locator('[data-menu="file"]').click();
    const saved=page.waitForEvent('download');await page.locator('#save-file').click();
    const file=await saved;
    const data=await fs.readFile(await file.path());
    await page.locator('#file-input').setInputFiles({name:'roundtrip.pix',mimeType:'application/json',buffer:data});
    await page.waitForFunction(()=>app.layers.length===4);
    assert.equal(await page.evaluate(()=>app.activeLayerId),null);
    await importPattern(1025,1,'wide.png');
    assert.deepEqual(await page.evaluate(()=>[app.canvasWidth,app.canvasHeight,app.layers.length]),[4,7,5]);
    assert.deepEqual(await page.evaluate(()=>Array.from(getActiveLayer().data.slice(3*4,3*4+4))),[3,0,91,255]);
    const topId=await page.evaluate(()=>app.layers[0].id);
    await page.locator('.layer-item').first().dragTo(page.locator('.layer-item').last());
    assert.equal(await page.evaluate(()=>app.layers.at(-1).id),topId);
    // Empty documents remain editable, serializable, and undoable.
    while(await page.locator('.layer-item').count()) await action('delete').click();
    assert.equal(await page.evaluate(()=>app.activeLayerId),null);
    const emptySave=page.waitForEvent('download');await page.locator('#save-file').click();
    const emptyBytes=await fs.readFile(await (await emptySave).path());
    assert.equal(JSON.parse(emptyBytes).layers.length,0);
    await page.locator('#file-input').setInputFiles({name:'empty.pix',mimeType:'application/json',buffer:emptyBytes});
    await page.waitForTimeout(100);
    assert.equal(await page.locator('.layer-item').count(),0);
    await importPattern(2,3,'from-empty.png');
    await page.locator('#undo-btn').click();
    assert.equal(await page.locator('.layer-item').count(),0);
    await page.locator('#redo-btn').click();
    assert.equal(await page.locator('.layer-item').count(),1);
    assert.deepEqual(errors,[]);
    console.log('PASS: physical layer-control clicks, deselection, deletion, locking, visibility, rename, history, exact new-image dimensions, and cropped/padded imports as added layers.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
