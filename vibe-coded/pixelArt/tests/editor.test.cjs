const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
(async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto('file://' + path.resolve('index.html'));
    const setChannel = (key, value) => page.locator('#number-' + key).fill(String(value));
    await page.locator('[data-menu="colors"]').click();
    await setChannel('h', 210); await setChannel('s', 75); await setChannel('v', 80);
    assert.deepEqual(await page.evaluate(() => app.color1), { r: 51, g: 128, b: 204, a: 255 });
    await setChannel('v', 0); await setChannel('v', 40);
    assert.equal(await page.locator('#number-h').inputValue(), '210');
    assert.equal(await page.locator('#number-s').inputValue(), '75');
    assert.deepEqual(await page.evaluate(() => app.color1), { r: 26, g: 64, b: 102, a: 255 });
    await setChannel('r', 123);
    assert.equal(await page.locator('#color1-picker').inputValue(), '#7b4066');
    await page.locator('#palette-add').click();
    assert.equal(await page.locator('.palette-entry').count(), 9);
    await page.reload();
    assert.equal(await page.evaluate(() => app.palette.at(-1).r), 123);
    await page.locator('[data-menu="colors"]').click();
    await page.locator('.palette-pick').last().click();
    assert.equal(await page.locator('#color1-picker').inputValue(), '#7b4066');
    await page.locator('[data-color-target="color2"]').click();
    await setChannel('r', 5); await setChannel('g', 10); await setChannel('b', 15);
    assert.equal(await page.locator('#color2-picker').inputValue(), '#050a0f');
    await page.locator('[data-menu="tools"]').click();
    await page.locator('[data-tool="brush"]').click();
    async function paint(x,y,button='left') {
      const point = await page.evaluate(({x,y}) => {
        const r = document.getElementById('canvas-area').getBoundingClientRect();
        return { x: r.left+app.panX+(x+.5)*app.zoom, y:r.top+app.panY+(y+.5)*app.zoom };
      },{x,y});
      await page.mouse.click(point.x,point.y,{button});
    }
    await paint(10,10);
    await paint(11,10,'right');
    assert.deepEqual(await page.evaluate(() => Array.from(getActiveLayer().data.slice((10*64+11)*4,(10*64+11)*4+4))),[5,10,15,255]);
    await page.keyboard.press('Control+z');
    assert.equal(await page.evaluate(() => getActiveLayer().data[(10*64+11)*4+3]),0);
    await page.keyboard.press('Control+Shift+z');
    assert.equal(await page.evaluate(() => getActiveLayer().data[(10*64+11)*4+3]),255);
    await page.locator('#tile-toggle').click();
    assert.equal(await page.evaluate(() => app.tileView),true);
    assert.equal(await page.locator('#artwork-viewport').isVisible(),true);
    await paint(64,20);
    assert.equal(await page.evaluate(() => getActiveLayer().data[(20*64)*4+3]),255);
    await page.evaluate(() => { app.brushSize=3; paintBrush(0,30,app.color1,3,'square'); render(); });
    assert.equal(await page.evaluate(() => getActiveLayer().data[(30*64+63)*4+3]),255);
    // The eyedropper preserves source RGBA exactly, including very low alpha.
    await page.evaluate(() => {
      const layer = getActiveLayer(); layer.data.fill(0);
      for (let a=1; a<=255; a++) {
        const x=a%64, y=Math.floor(a/64);
        layer.data.set([123,67,201,a],(y*64+x)*4);
      }
      render();
    });
    assert.equal(await page.evaluate(() => {
      for (let a=1;a<=255;a++) {
        const c=sampleArtwork(a%64,Math.floor(a/64));
        if (c.r!==123 || c.g!==67 || c.b!==201 || c.a!==a) return false;
      }
      return true;
    }),true);
    const samplePoint=await page.evaluate(() => {
      const r=document.getElementById('canvas-area').getBoundingClientRect();
      return {x:r.left+app.panX+1.5*app.zoom,y:r.top+app.panY+.5*app.zoom};
    });
    await page.keyboard.down('Alt');
    await page.mouse.click(samplePoint.x,samplePoint.y);
    await page.keyboard.up('Alt');
    assert.deepEqual(await page.evaluate(() => app.color1),{r:123,g:67,b:201,a:1});
    await page.locator('[data-menu="colors"]').click();
    assert.equal(await page.locator('#color1-picker').inputValue(),'#7b43c9');
    assert.equal(await page.locator('#hex-color').inputValue(),'#7b43c901');
    // The visible canvas-only eyedropper uses exactly the same raw-data path.
    await page.locator('[data-menu="tools"]').click();
    await page.locator('#eyedropper-btn').click();
    await page.mouse.click(samplePoint.x,samplePoint.y,{button:'right'});
    assert.deepEqual(await page.evaluate(() => app.color2),{r:123,g:67,b:201,a:1});
    assert.ok((await page.locator('#sampled-color-value').textContent()).includes('RGB 123, 67, 201'));
    await page.locator('[data-tool="brush"]').click();
    assert.equal(await page.locator('#eyedropper-tool-options').isVisible(),false);
    await page.locator('[data-menu="colors"]').click();
    await page.locator('[data-color-target="color1"]').click();
    // Merely refreshing HSV/RGB/native picker controls must never quantize RGB.
    await page.evaluate(() => { for(let i=0;i<20;i++) updateColorPickers(); });
    assert.deepEqual(await page.evaluate(() => app.color1),{r:123,g:67,b:201,a:1});
    // Swatches open the in-editor dialog; its picker samples the canvas too.
    await page.locator('#color1-open').click();
    assert.equal(await page.locator('#canvas-color-dialog').isVisible(),true);
    assert.equal(await page.locator('#color1-picker').getAttribute('type'),'text');
    await setChannel('r',7); await setChannel('g',153); await setChannel('b',227); await setChannel('a',53);
    await page.locator('#dialog-color-apply').click();
    await page.waitForFunction(()=>!colorDialog.open && !colorDialog.contains(colorControls));
    assert.deepEqual(await page.evaluate(()=>app.color1),{r:7,g:153,b:227,a:53});
    await page.locator('#color1-open').click();
    assert.equal(await page.locator('#hex-color').inputValue(),'#0799e335');
    await setChannel('r',200); await page.locator('#dialog-color-cancel').click();
    await page.waitForFunction(()=>!colorDialog.open && !colorDialog.contains(colorControls));
    assert.deepEqual(await page.evaluate(()=>app.color1),{r:7,g:153,b:227,a:53});
    await page.locator('#color2-open').click();
    await page.locator('#dialog-pick-canvas').click();
    await page.waitForFunction(()=>app.activeTool==='eyedropper' && !colorDialog.open);
    await page.mouse.click(samplePoint.x,samplePoint.y);
    assert.deepEqual(await page.evaluate(()=>app.color2),{r:123,g:67,b:201,a:1});
    assert.deepEqual(await page.evaluate(()=>app.color1),{r:7,g:153,b:227,a:53});
    await page.locator('[data-tool="brush"]').click();
    await page.evaluate(() => { app.color1={r:123,g:64,b:102,a:255}; });
    await page.locator('[data-menu="tools"]').click();
    // Export source-over compositing: an opaque lower layer survives transparent upper pixels.
    await page.evaluate(() => {
      app.tileView=false; app.layers=[]; addLayer('Bottom');
      paintPixel(2,2,{r:255,g:0,b:0,a:255});
      addLayer('Top'); paintPixel(2,2,{r:0,g:0,b:255,a:128}); render();
    });
    assert.deepEqual(await page.evaluate(() => Array.from(compositeArtwork().getContext('2d').getImageData(2,2,1,1).data)),[127,0,128,255]);
    assert.equal(await page.evaluate(() => compositeArtwork().getContext('2d').getImageData(0,0,1,1).data[3]),0);
    await page.locator('[data-menu="file"]').click();
    const downloadPromise = page.waitForEvent('download');
    await page.locator('#export-file').click();
    const download = await downloadPromise;
    const png = await fs.readFile(await download.path());
    assert.equal(png.subarray(1,4).toString(),'PNG');
    const savePromise = page.waitForEvent('download'); await page.locator('#save-file').click();
    const saved = await savePromise;
    const projectText = await fs.readFile(await saved.path(),'utf8');
    const project = JSON.parse(projectText);
    assert.equal(project.palette.length,9);
    await page.locator('#file-input').setInputFiles({name:'roundtrip.pix', mimeType:'application/json', buffer:Buffer.from(projectText)});
    await page.waitForFunction(() => app.layers.length===2);
    assert.equal(await page.evaluate(() => app.layers[0].name),'Top');
    // Selection previews, rotation, clipboard, and committing changed pixels.
    await page.evaluate(() => {
      app.selection={x:1,y:1,width:4,height:4}; applySelection(); app.selectionConfirmed=true;
      app.transformMode='rotate'; app.transform.angle=30; transformSelection();
    });
    assert.ok(await page.evaluate(() => app.selection.data.length > 0));
    await page.evaluate(() => { copySelection(); clearSelection(); pasteSelection(); });
    assert.equal(await page.evaluate(() => app.selection.data.some(n=>n>0)),true);
    await page.evaluate(() => { app.selection.x=20; app.selection.y=20; commitSelection(); });
    assert.equal(await page.evaluate(() => app.selection),null);
    await page.locator('#layer-list .layer-btn').first().click();
    assert.equal(await page.locator('.layer-item').count(),2);
    await page.locator('#layer-list .layer-btn').first().click();
    // Resize preserves the intersection and undo restores original dimensions.
    await page.evaluate(() => resizeDocument(32,48));
    assert.deepEqual(await page.evaluate(() => [app.canvasWidth,app.canvasHeight,getActiveLayer().data.length]),[32,48,32*48*4]);
    await page.evaluate(() => undo());
    assert.equal(await page.evaluate(() => app.canvasWidth),64);
    await page.locator('#duplicate-layer-btn').click();
    assert.equal(await page.evaluate(() => app.layers.length),3);
    await page.evaluate(() => undo());
    assert.equal(await page.evaluate(() => app.layers.length),2);
    // Shape commits through tool switching, with all four rectangle edges.
    await page.locator('[data-menu="tools"]').click();
    await page.locator('[data-tool="shape"]').click();
    await page.evaluate(() => { app.shapeEditing={type:'rect',x:40,y:40,width:5,height:5,outlineWidth:1,filled:false}; });
    await page.locator('[data-tool="brush"]').click();
    assert.equal(await page.evaluate(() => getActiveLayer().data[(44*64+44)*4+3]),255);
    // Selection effects change the actual preview pixels.
    await page.evaluate(() => {
      app.selection={x:40,y:40,width:5,height:5}; applySelection(); app.selectionConfirmed=true;
      app.colorizeColor={r:250,g:20,b:30,a:255}; app.colorizeInterpolation=100;
      document.getElementById('colorize-btn').click();
    });
    assert.deepEqual(await page.evaluate(() => Array.from(app.selection.data.slice(0,3))),[121,10,14]);
    await page.evaluate(() => {
      app.ditherSettings.colorRes=true; app.ditherSettings.palette=[{r:0,g:0,b:0},{r:255,g:255,b:255}];
      document.getElementById('dither-btn').click();
    });
    assert.ok(await page.evaluate(() => [0,255].includes(app.selection.data[0])));
    await page.evaluate(() => clearSelection());
    // Colorize preserves shading and alpha, with interpolation at both endpoints
    // and halfway. Non-gray sources distinguish HSV value from luminance.
    const colorizePixels = [0,0,0,255, 255,255,255,255, 128,128,128,97, 40,160,80,1, 90,45,180,0];
    for (const [factor, expected] of [
      [0, colorizePixels],
      [100, [0,0,0,255, 204,102,51,255, 102,51,26,97, 128,64,32,1, 144,72,36,0]],
      [50, [0,0,0,255, 230,179,153,255, 115,90,77,97, 84,112,56,1, 117,59,108,0]]
    ]) {
      await page.evaluate(pixels => {
        app.selection={x:0,y:0,width:5,height:1,data:new Uint8ClampedArray(pixels)};
        app.selectionConfirmed=true; app.colorizeColor={r:204,g:102,b:51,a:255};
      }, colorizePixels);
      await page.locator('#colorize-interpolation').evaluate((input, value) => {
        input.value=String(value); input.dispatchEvent(new Event('input',{bubbles:true}));
      }, factor);
      assert.equal(await page.locator('#colorize-interpolation-val').textContent(),factor+'%');
      await page.evaluate(() => document.getElementById('colorize-btn').click());
      assert.deepEqual(await page.evaluate(() => Array.from(app.selection.data)),expected);
      assert.deepEqual(await page.evaluate(() => Array.from(app.selection.source)),expected);
    }
    await page.evaluate(() => clearSelection());
    // Scaled PNG export writes requested physical dimensions.
    await page.locator('[data-menu="file"]').click();
    await page.locator('#export-as').click();
    await page.locator('#export-scale').fill('3');
    const scaledPromise=page.waitForEvent('download'); await page.locator('#dialog-ok').click();
    const scaled=await fs.readFile(await (await scaledPromise).path());
    assert.equal(scaled.readUInt32BE(16),192); assert.equal(scaled.readUInt32BE(20),192);
    // Importing a different-size PNG adds a document-sized layer without resizing.
    const importBuffer=await page.evaluate(() => {
      const c=document.createElement('canvas');c.width=12;c.height=8;
      c.getContext('2d').fillRect(0,0,12,8);return c.toDataURL().split(',')[1];
    });
    await page.locator('#file-input').setInputFiles({name:'tiny.png',mimeType:'image/png',buffer:Buffer.from(importBuffer,'base64')});
    await page.waitForFunction(() => app.layers.length===3);
    assert.deepEqual(await page.evaluate(() => [app.canvasWidth,app.canvasHeight,app.layers.length,getActiveLayer().data.length]),[64,64,3,64*64*4]);
    await page.evaluate(() => undo());
    assert.equal(await page.evaluate(() => app.canvasWidth),64);
    // Malformed projects must not mutate the document.
    await page.locator('#file-input').setInputFiles({name:'bad.pix',mimeType:'application/json',buffer:Buffer.from('{"canvasWidth":-5}')});
    await page.waitForTimeout(100);
    assert.equal(await page.evaluate(() => app.canvasWidth),64);
    assert.deepEqual(errors,[]);
    await page.locator('[data-menu="colors"]').click();
    await page.screenshot({path:'/tmp/pixelforge-colors.png'});
    console.log('PASS: color synchronization, HSV memory, palette persistence, painting, undo/redo, tiling, compositing, PNG/project I/O, selections, and hidden layers.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; });
