'use strict';

// Enhancements share the editor's document state; RGB remains the paint format.
app.activeColor = 'color1';
app.tileView = false;
app.palette = ['#171c2c', '#ffffff', '#5964a0', '#4a9eff', '#55d6be', '#f8d477', '#ed8a6b', '#be6288'].map(hex => ({ ...hexToRgb(hex), a: 255 }));
try {
  const saved = JSON.parse(localStorage.getItem('pixelforge.palette'));
  if (Array.isArray(saved) && saved.length <= 256 && saved.every(validColor)) app.palette = saved;
} catch { /* Storage is optional. */ }
const colorMemory = { color1: null, color2: null };
const history = { undo: [], redo: [], restoring: false };

function validColor(c) {
  return c && ['r', 'g', 'b', 'a'].every(k => Number.isInteger(c[k]) && c[k] >= 0 && c[k] <= 255);
}
function validateProject(data) {
  if (![data.canvasWidth, data.canvasHeight].every(n => Number.isInteger(n) && n >= 1 && n <= 1024) ||
      !Array.isArray(data.layers) || data.layers.length > 32 ||
      !validColor(data.color1) || !validColor(data.color2) ||
      !data.layers.every(l => typeof l.id === 'string' && typeof l.name === 'string' && Array.isArray(l.data) &&
        l.data.length === data.canvasWidth * data.canvasHeight * 4 && l.data.every(n => Number.isInteger(n) && n >= 0 && n <= 255)) ||
      new Set(data.layers.map(l => l.id)).size !== data.layers.length ||
      (data.activeLayerId !== null && !data.layers.some(l => l.id === data.activeLayerId)) ||
      (data.palette !== undefined && (!Array.isArray(data.palette) || data.palette.length > 256 || !data.palette.every(validColor)))) {
    throw new Error('Invalid PixelForge project');
  }
}
function snapshot() {
  return { canvasWidth: app.canvasWidth, canvasHeight: app.canvasHeight, activeLayerId: app.activeLayerId,
    layers: app.layers.map(l => ({ ...l, data: new Uint8ClampedArray(l.data) })) };
}
function pushHistory() {
  if (history.restoring) return;
  history.undo.push(snapshot());
  // Limit by memory as well as count (large documents can have many layers).
  let bytes = 0;
  for (let i = history.undo.length - 1; i >= 0; i--) {
    bytes += history.undo[i].layers.reduce((n, l) => n + l.data.byteLength, 0);
    if ((bytes > 64 * 1024 * 1024 || history.undo.length - i > 40) && i < history.undo.length - 1) {
      history.undo.splice(0, i + 1); break;
    }
  }
  history.redo = [];
  updateHistoryButtons();
}
function restoreHistory(from, to) {
  if (!from.length) return;
  to.push(snapshot());
  history.restoring = true;
  app.shapeEditing = null;
  clearSelection();
  Object.assign(app, from.pop());
  updateCanvas(); updateLayerList(); fitCanvasToView();
  history.restoring = false;
  updateHistoryButtons();
}
function undo() { restoreHistory(history.undo, history.redo); }
function redo() { restoreHistory(history.redo, history.undo); }
function updateHistoryButtons() {
  for (const key of ['undo', 'redo']) {
    const button = document.getElementById(key + '-btn');
    if (button) button.disabled = !history[key].length;
  }
}
function compositeArtwork() {
  const canvas = document.createElement('canvas');
  canvas.width = app.canvasWidth; canvas.height = app.canvasHeight;
  const ctx = canvas.getContext('2d');
  const source = document.createElement('canvas');
  source.width = canvas.width; source.height = canvas.height;
  const sourceCtx = source.getContext('2d');
  app.layers.slice().reverse().forEach(layer => {
    if (!layer.visible) return;
    sourceCtx.putImageData(new ImageData(new Uint8ClampedArray(layer.data), canvas.width, canvas.height), 0, 0);
    ctx.drawImage(source, 0, 0);
  });
  return canvas;
}

// Read paint bytes directly. A canvas round trip can change RGB by several
// levels at low alpha because browsers store premultiplied color internally.
function sampleArtwork(x, y) {
  if (x < 0 || y < 0 || x >= app.canvasWidth || y >= app.canvasHeight) return null;
  const index = (Math.floor(y) * app.canvasWidth + Math.floor(x)) * 4;
  let r = 0, g = 0, b = 0, alpha = 0;
  for (const layer of app.layers.slice().reverse()) {
    if (!layer.visible) continue;
    const a = layer.data[index + 3] / 255;
    if (!a) continue;
    r = layer.data[index] * a + r * (1 - a);
    g = layer.data[index + 1] * a + g * (1 - a);
    b = layer.data[index + 2] * a + b * (1 - a);
    alpha = a + alpha * (1 - a);
  }
  return alpha ? { r: Math.round(r / alpha), g: Math.round(g / alpha), b: Math.round(b / alpha), a: Math.round(alpha * 255) } : { r: 0, g: 0, b: 0, a: 0 };
}

// HSV's value is presented as intensity/brightness. Remember H and S at black,
// and H at gray, so changing one slider never resets the other two.
function rgbToHsv(c, previous) {
  const r = c.r / 255, g = c.g / 255, b = c.b / 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), delta = max - min;
  let h = previous?.h || 0;
  if (delta) {
    h = max === r ? ((g - b) / delta) % 6 : max === g ? (b - r) / delta + 2 : (r - g) / delta + 4;
    h = (h * 60 + 360) % 360;
  }
  return { h, s: max ? delta / max * 100 : previous?.s || 0, v: max * 100 };
}
function hsvToRgb(h, s, v) {
  s /= 100; v /= 100;
  const chroma = v * s, x = chroma * (1 - Math.abs((h / 60) % 2 - 1)), m = v - chroma;
  const values = h < 60 ? [chroma,x,0] : h < 120 ? [x,chroma,0] : h < 180 ? [0,chroma,x] : h < 240 ? [0,x,chroma] : h < 300 ? [x,0,chroma] : [chroma,0,x];
  return Object.fromEntries(['r','g','b'].map((k, i) => [k, Math.round((values[i] + m) * 255 + 1e-9)]));
}
function activeHsv() {
  const c = app[app.activeColor], signature = `${c.r},${c.g},${c.b}`;
  let memory = colorMemory[app.activeColor];
  if (!memory || memory.signature !== signature) {
    memory = { ...rgbToHsv(c, memory), signature };
    colorMemory[app.activeColor] = memory;
  }
  return memory;
}
function setActiveColor(color) {
  app[app.activeColor] = { ...color };
  updateColorPickers();
}

const leftPanel = document.getElementById('left-panel');
leftPanel.insertAdjacentHTML('afterbegin', '<div class="brand">Pixel<span>Forge</span><small>YOUR NEXT PIXEL STARTS HERE</small></div>');
const colorsMenu = document.getElementById('menu-colors');
colorsMenu.insertAdjacentHTML('afterbegin', `
  <div class="menu-section-title">Edit paint color</div>
  <div class="color-targets"><button class="btn active" data-color-target="color1">Primary · left</button><button class="btn" data-color-target="color2">Secondary · right</button></div>
  <div class="color-controls">
    <div class="hex-row"><label for="hex-color">Hex</label><input id="hex-color" maxlength="9" spellcheck="false" aria-label="Hex color" value="#000000"></div>
    <div class="menu-section-title">Hue / saturation / intensity</div>
    <p class="hint">Intensity is HSV brightness (value). Adjust each channel independently.</p>
    <div id="hsv-channels"></div>
    <div class="menu-section-title" style="margin-top:16px">RGB</div>
    <div id="rgb-channels"></div>
    <div id="alpha-channel"></div>
  </div>
  <div class="menu-section">
    <div class="menu-section-title">My palette <span id="palette-count"></span></div>
    <div class="btn-row"><button class="btn" id="palette-add">+ Add current</button><button class="btn" id="palette-extract">From image</button></div>
    <p class="hint">Pick a swatch to use it. Right-click for the secondary color. Your color list is saved automatically.</p>
    <div id="saved-palette"></div>
  </div>`);
function channelControl(key, label, max, parent) {
  document.getElementById(parent).insertAdjacentHTML('beforeend', `<div class="channel-row"><label for="channel-${key}">${label}</label><input type="range" id="channel-${key}" min="0" max="${max}" step="1" aria-label="${label}" value="0"><input class="channel-number" id="number-${key}" type="number" min="0" max="${max}" step="1" aria-label="${label} value" value="0"></div>`);
  for (const prefix of ['channel-', 'number-']) {
    document.getElementById(prefix + key).addEventListener('input', e => {
      if (e.target.value === '' || !Number.isFinite(e.target.valueAsNumber)) return;
      const value = Math.round(Math.max(0, Math.min(max, e.target.valueAsNumber)));
      if ('hsv'.includes(key)) {
        const hsv = activeHsv();
        hsv[key] = value;
        const rgb = hsvToRgb(hsv.h, hsv.s, hsv.v);
        hsv.signature = `${rgb.r},${rgb.g},${rgb.b}`;
        Object.assign(app[app.activeColor], rgb);
      } else app[app.activeColor][key] = value;
      updateColorPickers();
    });
    document.getElementById(prefix + key).addEventListener('change', updateColorPickers);
  }
}
channelControl('h', 'Hue', 360, 'hsv-channels');
channelControl('s', 'Saturation', 100, 'hsv-channels');
channelControl('v', 'Intensity', 100, 'hsv-channels');
for (const [key, label] of [['r','Red'],['g','Green'],['b','Blue']]) channelControl(key, label, 255, 'rgb-channels');
channelControl('a', 'Alpha', 255, 'alpha-channel');
document.getElementById('hex-color').addEventListener('change', e => {
  let hex = e.target.value.trim();
  if (/^#?[\da-f]{3}$/i.test(hex)) hex = '#' + hex.replace('#', '').split('').map(c => c+c).join('');
  const match = /^#?([\da-f]{6})([\da-f]{2})?$/i.exec(hex);
  if (!match) { showToast('Use #RRGGBB or #RRGGBBAA'); updateColorPickers(); return; }
  setActiveColor({ ...hexToRgb(match[1]), a: match[2] ? parseInt(match[2], 16) : app[app.activeColor].a });
});
document.querySelectorAll('[data-color-target]').forEach(button => {
  button.onclick = () => { app.activeColor = button.dataset.colorTarget; updateColorPickers(); };
});
for (const target of ['color1', 'color2']) {
  document.getElementById(target + '-picker').addEventListener('click', () => {
    app.activeColor = target;
    updateColorPickers(); // Native picker always opens on that slot's current RGB.
  });
}
const originalUpdateColorPickers = updateColorPickers;
updateColorPickers = function() {
  originalUpdateColorPickers();
  for (const target of ['color1', 'color2']) {
    const c = app[target];
    document.getElementById(target + '-preview').textContent = rgbToHex(c.r,c.g,c.b).toUpperCase();
  }
  const c = app[app.activeColor], hsv = activeHsv();
  document.querySelectorAll('[data-color-target]').forEach(button => {
    const active = button.dataset.colorTarget === app.activeColor;
    button.classList.toggle('active', active); button.setAttribute('aria-pressed', active);
  });
  document.getElementById('hex-color').value = rgbToHex(c.r,c.g,c.b) + (c.a < 255 ? c.a.toString(16).padStart(2,'0') : '');
  for (const key of ['h','s','v','r','g','b','a']) {
    const value = Math.round('hsv'.includes(key) ? hsv[key] : c[key]);
    document.getElementById('channel-' + key).value = value;
    document.getElementById('number-' + key).value = value;
  }
  const hue = hsvToRgb(hsv.h,100,100);
  const hueHex = rgbToHex(hue.r,hue.g,hue.b);
  document.getElementById('channel-h').style.background = 'linear-gradient(to right, red, yellow, lime, cyan, blue, magenta, red)';
  document.getElementById('channel-s').style.background = `linear-gradient(to right, #888, ${hueHex})`;
  document.getElementById('channel-v').style.background = `linear-gradient(to right, #000, ${hueHex})`;
  for (const key of ['r','g','b']) {
    const low = { ...c, [key]: 0 }, high = { ...c, [key]: 255 };
    document.getElementById('channel-' + key).style.background = `linear-gradient(to right, ${rgbToHex(low.r,low.g,low.b)}, ${rgbToHex(high.r,high.g,high.b)})`;
  }
  renderSavedPalette();
};
function persistPalette() {
  try { localStorage.setItem('pixelforge.palette', JSON.stringify(app.palette)); } catch { /* Can still save in project. */ }
}
function renderSavedPalette() {
  const list = document.getElementById('saved-palette');
  list.replaceChildren();
  document.getElementById('palette-count').textContent = `(${app.palette.length}/256)`;
  app.palette.forEach((color, index) => {
    const row = document.createElement('div'); row.className = 'palette-entry';
    const button = document.createElement('button'); button.className = 'palette-pick';
    const hex = rgbToHex(color.r,color.g,color.b).toUpperCase();
    button.title = `Use ${hex} · alpha ${color.a}`;
    button.classList.toggle('selected', ['r','g','b','a'].every(k => color[k] === app[app.activeColor][k]));
    const chip = document.createElement('span'); chip.className = 'palette-chip';
    chip.style.background = `rgba(${color.r},${color.g},${color.b},${color.a/255})`;
    button.append(chip, document.createTextNode(hex + (color.a < 255 ? ` · ${Math.round(color.a/255*100)}%` : '')));
    button.onclick = () => setActiveColor(color);
    button.oncontextmenu = e => { e.preventDefault(); app.activeColor = 'color2'; setActiveColor(color); };
    const remove = document.createElement('button'); remove.className = 'layer-btn'; remove.textContent = '×'; remove.title = `Remove ${hex}`; remove.setAttribute('aria-label', remove.title);
    remove.onclick = () => { app.palette.splice(index,1); persistPalette(); renderSavedPalette(); };
    row.append(button, remove); list.append(row);
  });
}
document.getElementById('palette-add').onclick = () => {
  const c = app[app.activeColor];
  if (app.palette.some(color => ['r','g','b','a'].every(k => c[k] === color[k]))) { showToast('Color is already in your palette'); return; }
  if (app.palette.length >= 256) { showToast('Palette is full (256 colors)'); return; }
  app.palette.push({ ...c }); persistPalette(); renderSavedPalette();
};
document.getElementById('palette-extract').onclick = () => {
  const pixels = compositeArtwork().getContext('2d').getImageData(0,0,app.canvasWidth,app.canvasHeight).data;
  const counts = new Map();
  for (let i = 0; i < pixels.length; i += 4) {
    if (!pixels[i+3]) continue;
    const key = Array.from(pixels.slice(i,i+4)).join(','); counts.set(key, (counts.get(key)||0)+1);
  }
  const existing = new Set(app.palette.map(c => [c.r,c.g,c.b,c.a].join(',')));
  let added = 0;
  for (const [key] of [...counts].sort((a,b) => b[1]-a[1])) {
    if (app.palette.length >= 256 || added >= 32) break;
    if (existing.has(key)) continue;
    const [r,g,b,a] = key.split(',').map(Number); app.palette.push({r,g,b,a}); added++;
  }
  persistPalette(); renderSavedPalette(); showToast(`Added ${added} image colors`);
};

// This eyedropper reads the document, never screen or display-managed pixels.
const eyedropperButton = document.createElement('button');
eyedropperButton.className = 'btn tool-btn';
eyedropperButton.id = 'eyedropper-btn';
eyedropperButton.dataset.tool = 'eyedropper';
eyedropperButton.title = 'Pick canvas color (I) — Alt + click with any tool';
eyedropperButton.setAttribute('aria-label', 'Pick canvas color');
eyedropperButton.innerHTML = '<svg viewBox="0 0 24 24"><path d="m15 3 6 6M14 4l-2 2 6 6 2-2M13 7 4 16v4h4l9-9M4 20l-1 1"/></svg>';
document.querySelector('[data-tool="shape"]').after(eyedropperButton);
const eyedropperOptions = document.createElement('div');
eyedropperOptions.id = 'eyedropper-tool-options';
eyedropperOptions.className = 'menu-section hidden';
eyedropperOptions.innerHTML = '<div class="menu-section-title">Pick canvas color</div><p class="hint">Click the artwork to pick its exact color. Right-click to set the secondary color. Colors come from the canvas data and are unaffected by Night Light or screen tint.</p><div id="sampled-color-value" class="hint" aria-live="polite"></div>';
document.getElementById('menu-tools').append(eyedropperOptions);
eyedropperButton.onclick = () => {
  if (app.shapeEditing) commitShape();
  clearSelection();
  app.activeTool = 'eyedropper';
  document.querySelectorAll('.tool-btn').forEach(button => button.classList.toggle('active', button === eyedropperButton));
  for (const tool of ['select','brush','eraser','shape']) document.getElementById(tool+'-tool-options').classList.add('hidden');
  eyedropperOptions.classList.remove('hidden');
};
document.querySelectorAll('.tool-btn:not(#eyedropper-btn)').forEach(button => button.addEventListener('click', () => eyedropperOptions.classList.add('hidden')));
document.addEventListener('keydown', e => {
  if (e.key.toLowerCase() === 'i' && !e.ctrlKey && !e.metaKey && !e.altKey && !e.target.matches('input, textarea, select') && !document.querySelector('.dialog-overlay, dialog[open]')) eyedropperButton.click();
});

// Live repeated artwork, with the central tile remaining the edit origin.
document.getElementById('canvas-area').insertAdjacentHTML('beforeend', `
  <div class="workspace-toolbar"><button class="btn" id="undo-btn" title="Undo (Ctrl+Z)">↶ Undo</button><button class="btn" id="redo-btn" title="Redo (Ctrl+Shift+Z)">↷ Redo</button><span class="spacer"></span><button class="btn" id="fit-btn">Fit</button><button class="btn" id="tile-toggle" title="Tiled view (T)" aria-pressed="false">▦ Tile view</button></div>
  <div id="canvas-status"></div>`);
document.getElementById('right-panel').insertAdjacentHTML('beforeend', `
  <div id="tile-preview-section"><div class="menu-section-title">Live tile preview</div><canvas id="tile-preview" width="432" height="380" aria-label="Live tiled artwork preview"></canvas><div class="slider-label" style="margin-top:10px"><label for="preview-repeats">Repeats</label><select id="preview-repeats"><option value="2">2 × 2</option><option value="3" selected>3 × 3</option><option value="5">5 × 5</option></select></div><p class="hint">Toggle Tile view to paint across seams.<br>T: tile view · X: swap colors<br>Alt + click: sample color</p></div>`);
const tileCopies = [];
for (let y = -1; y <= 1; y++) for (let x = -1; x <= 1; x++) {
  if (!x && !y) continue;
  const canvas = document.createElement('canvas'); canvas.className = 'tile-copy';
  document.getElementById('canvas-container').prepend(canvas);
  tileCopies.push({ canvas, x, y });
}
function renderTileViews() {
  for (const { canvas, x, y } of tileCopies) {
    canvas.hidden = !app.tileView;
    if (!app.tileView) continue;
    if (canvas.width !== app.canvasWidth) canvas.width = app.canvasWidth;
    if (canvas.height !== app.canvasHeight) canvas.height = app.canvasHeight;
    canvas.style.left = x * app.canvasWidth + 'px'; canvas.style.top = y * app.canvasHeight + 'px';
    const ctx = canvas.getContext('2d'); ctx.clearRect(0,0,canvas.width,canvas.height); ctx.drawImage(mainCanvas,0,0);
  }
  const preview = document.getElementById('tile-preview'), ctx = preview.getContext('2d');
  const count = Number(document.getElementById('preview-repeats').value);
  const scale = Math.min(preview.width/(app.canvasWidth*count),preview.height/(app.canvasHeight*count));
  const w = app.canvasWidth * scale, h = app.canvasHeight * scale;
  const ox = (preview.width-w*count)/2, oy = (preview.height-h*count)/2;
  ctx.clearRect(0,0,preview.width,preview.height); ctx.imageSmoothingEnabled = false;
  // Round shared boundaries to avoid subpixel gaps between repeats.
  for (let y=0; y<count; y++) for (let x=0; x<count; x++) {
    const left = Math.round(ox+x*w), top = Math.round(oy+y*h);
    ctx.drawImage(mainCanvas,left,top,Math.round(ox+(x+1)*w)-left,Math.round(oy+(y+1)*h)-top);
  }
  const toggle = document.getElementById('tile-toggle');
  toggle.classList.toggle('active', app.tileView); toggle.setAttribute('aria-pressed',app.tileView);
}
const originalRender = render;
render = function() { originalRender(); renderTileViews(); };
const originalTransform = updateTransform;
updateTransform = function() {
  originalTransform();
  document.getElementById('canvas-status').textContent = `${app.canvasWidth} × ${app.canvasHeight} px  ·  ${Math.round((app.displayScaleX || app.zoom)*100)}%${app.tileView ? '  ·  3 × 3 tiles' : ''}`;
};
fitCanvasToView = function() {
  const area = document.getElementById('canvas-area');
  const width = area.clientWidth, height = area.clientHeight;
  const repeats = app.tileView ? 3 : 1;
  app.zoom = Math.max(.1, Math.min(64, (width-80)/(app.canvasWidth*repeats),(height-130)/(app.canvasHeight*repeats)));
  if (app.zoom >= 1) app.zoom = Math.floor(app.zoom);
  app.panX = (width-app.canvasWidth*app.zoom)/2;
  app.panY = (height-app.canvasHeight*app.zoom)/2;
  updateTransform(); renderOverlay();
};
document.getElementById('tile-toggle').onclick = () => { app.tileView = !app.tileView; fitCanvasToView(); render(); };
document.getElementById('fit-btn').onclick = fitCanvasToView;
document.getElementById('undo-btn').onclick = undo;
document.getElementById('redo-btn').onclick = redo;
document.getElementById('preview-repeats').onchange = renderTileViews;
window.addEventListener('resize', fitCanvasToView);
window.addEventListener('blur', () => { app.ctrlPressed = false; app.shiftPressed = false; });

const originalPaintPixel = paintPixel;
paintPixel = function(x,y,color) {
  if (app.tileView) { x = ((x % app.canvasWidth)+app.canvasWidth)%app.canvasWidth; y = ((y % app.canvasHeight)+app.canvasHeight)%app.canvasHeight; }
  return originalPaintPixel(x,y,color);
};
const originalMouseDown = document.getElementById('canvas-area').onmousedown;
document.getElementById('canvas-area').onmousedown = e => {
  if (e.target.closest('.workspace-toolbar')) return;
  const {x,y} = getCanvasCoords(e), n = app.tileView ? 1 : 0;
  if (x < -n*app.canvasWidth || x >= (n+1)*app.canvasWidth || y < -n*app.canvasHeight || y >= (n+1)*app.canvasHeight) return;
  if (e.altKey || app.activeTool === 'eyedropper') {
    const cx = ((x%app.canvasWidth)+app.canvasWidth)%app.canvasWidth, cy = ((y%app.canvasHeight)+app.canvasHeight)%app.canvasHeight;
    const color = sampleArtwork(cx,cy);
    app.activeColor = pendingCanvasColorTarget || (e.button === 2 ? 'color2' : 'color1');
    pendingCanvasColorTarget = null;
    setActiveColor(color);
    document.getElementById('sampled-color-value').textContent = `${rgbToHex(color.r,color.g,color.b).toUpperCase()} · RGB ${color.r}, ${color.g}, ${color.b} · Alpha ${color.a}`;
    return;
  }
  if (e.button !== 0 && e.button !== 2) return;
  if (app.activeTool !== 'brush' && app.activeTool !== 'eraser' && (x<0 || y<0 || x>=app.canvasWidth || y>=app.canvasHeight)) return;
  if (!getActiveLayer()) { showToast('Select a layer to edit'); return; }
  if (getActiveLayer().locked) return;
  if (app.activeTool === 'brush' || app.activeTool === 'eraser') pushHistory();
  originalMouseDown(e);
};
const originalMouseUp = document.getElementById('canvas-area').onmouseup;
document.getElementById('canvas-area').onmouseup = e => { originalMouseUp(e); updateLayerList(); };
const originalAddLayer = addLayer;
addLayer = function(name, recordHistory = true) {
  if (app.layers.length >= 32) { showToast('Maximum of 32 layers'); return null; }
  if (recordHistory) pushHistory();
  return originalAddLayer(name);
};
const originalRemoveLayer = removeLayer;
removeLayer = function(id) { if (app.layers.some(layer => layer.id === id)) pushHistory(); originalRemoveLayer(id); };
const originalCommitShape = commitShape;
commitShape = function() { if (app.shapeEditing && !getActiveLayer()?.locked) pushHistory(); originalCommitShape(); updateLayerList(); };
// A named palette color can be used for dithering too; adding uses the current paint.
document.getElementById('add-palette-color').onclick = () => { app.ditherSettings.palette.push({...app[app.activeColor]}); updatePaletteUI(); };

updateColorPickers(); render(); fitCanvasToView(); updateHistoryButtons();

// Selection edits stay in a preview until Apply; Cancel leaves the layer intact.
document.getElementById('transform-section').insertAdjacentHTML('afterbegin', '<div class="btn-row"><button class="btn active" id="apply-selection">Apply edits</button><button class="btn" id="discard-selection">Cancel</button></div>');
applySelection = function() {
  if (!app.selection || getActiveLayer()?.locked) return;
  const sel = app.selection;
  const x = Math.max(0, Math.floor(sel.x)), y = Math.max(0, Math.floor(sel.y));
  const w = Math.min(app.canvasWidth, Math.ceil(sel.x+sel.width))-x;
  const h = Math.min(app.canvasHeight, Math.ceil(sel.y+sel.height))-y;
  if (w<=0 || h<=0) { clearSelection(); return; }
  const data = new Uint8ClampedArray(w*h*4), layer = getActiveLayer();
  for (let dy=0; dy<h; dy++) {
    const start = ((y+dy)*app.canvasWidth+x)*4;
    data.set(layer.data.slice(start,start+w*4),dy*w*4);
  }
  Object.assign(sel, {x,y,width:w,height:h,data,source:new Uint8ClampedArray(data),sourceWidth:w,sourceHeight:h,origin:{x,y,width:w,height:h}, layerId:layer.id});
  app.transform = {x:0,y:0,w,h,angle:0,skewX:0,skewY:0};
  for (const [id,value] of [['trans-x',0],['trans-y',0],['trans-w',w],['trans-h',h],['trans-angle',0],['skew-x',0],['skew-y',0]]) {
    document.getElementById(id).value = value;
    document.getElementById(id+'-val').textContent = value;
  }
};
const oldDone = document.getElementById('done-selection').onclick;
document.getElementById('done-selection').onclick = () => { oldDone(); if (!app.selection) app.selectionConfirmed = false; render(); };
const originalRenderOverlay = renderOverlay;
renderOverlay = function() {
  originalRenderOverlay();
  const sel = app.selection;
  if (!sel?.data || !app.selectionConfirmed) return;
  const canvas = document.createElement('canvas'); canvas.width = sel.width; canvas.height = sel.height;
  canvas.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(sel.data),sel.width,sel.height),0,0);
  overlayCtx.save(); overlayCtx.globalCompositeOperation = 'destination-over';
  overlayCtx.drawImage(canvas,Math.floor(sel.x),Math.floor(sel.y)); overlayCtx.restore();
};
transformSelection = function() {
  const sel = app.selection;
  if (!sel?.data) return;
  const source = sel.source || sel.data, w = sel.sourceWidth || sel.width, h = sel.sourceHeight || sel.height;
  let result;
  if (app.transformMode === 'resize') result = transformResize(source,w,h,Math.min(2048,Math.max(1,app.transform.w)),Math.min(2048,Math.max(1,app.transform.h)));
  if (app.transformMode === 'rotate') result = transformRotate(source,w,h,app.transform.angle);
  if (app.transformMode === 'skew') result = transformSkew(source,w,h,app.transform.skewX,app.transform.skewY);
  if (result) { sel.data = result.data; sel.width = result.width; sel.height = result.height; app.transform.w = result.width; app.transform.h = result.height; }
  renderOverlay();
};
commitSelection = function() {
  const sel = app.selection;
  const layer = app.layers.find(l => l.id === (sel?.layerId || app.activeLayerId));
  if (!sel?.data || !layer || layer.locked) return;
  pushHistory();
  if (sel.origin) {
    const o = sel.origin;
    for (let y=o.y; y<o.y+o.height; y++) for (let x=o.x; x<o.x+o.width; x++) {
      const i = (y*app.canvasWidth+x)*4;
      layer.data.set([app.color2.r,app.color2.g,app.color2.b,app.color2.a],i);
    }
  }
  const canvas = document.createElement('canvas'); canvas.width = app.canvasWidth; canvas.height = app.canvasHeight;
  const ctx = canvas.getContext('2d'); ctx.putImageData(new ImageData(layer.data,app.canvasWidth,app.canvasHeight),0,0);
  const selected = document.createElement('canvas'); selected.width = sel.width; selected.height = sel.height;
  selected.getContext('2d').putImageData(new ImageData(sel.data,sel.width,sel.height),0,0);
  ctx.drawImage(selected,Math.floor(sel.x),Math.floor(sel.y));
  layer.data = ctx.getImageData(0,0,canvas.width,canvas.height).data;
  clearSelection(); updateLayerList(); render();
};
document.getElementById('apply-selection').onclick = commitSelection;
document.getElementById('discard-selection').onclick = () => { clearSelection(); render(); };
const originalClearSelection = clearSelection;
clearSelection = function() { originalClearSelection(); render(); };
for (const axis of ['x','y']) {
  document.getElementById('trans-'+axis).oninput = e => {
    app.transform[axis] = Number(e.target.value);
    document.getElementById('trans-'+axis+'-val').textContent = e.target.value;
    if (app.selectionConfirmed && app.selection) {
      app.selection[axis] = (app.selection.origin?.[axis] || 0) + app.transform[axis]; renderOverlay();
    }
  };
}
const oldPasteSelection = pasteSelection;
pasteSelection = function() {
  oldPasteSelection();
  if (app.selection?.data) {
    const sel = app.selection;
    sel.source = new Uint8ClampedArray(sel.data); sel.sourceWidth = sel.width; sel.sourceHeight = sel.height; sel.layerId = app.activeLayerId;
  }
};
// Buttons originally held direct references, so wire them to the completed flow.
document.getElementById('paste-selection').onclick = () => pasteSelection();
document.getElementById('cancel-selection').onclick = () => clearSelection();

document.getElementById('colorize-btn').onclick = () => {
  const sel = app.selection;
  if (!sel?.data || !app.selectionConfirmed) { showToast('Select an area first'); return; }
  const factor = app.colorizeInterpolation/100, color = app.colorizeColor;
  for (let i=0; i<sel.data.length; i+=4) {
    // Use the original pixel's HSV value for all channels before writing RGB.
    const intensity = Math.max(sel.data[i], sel.data[i+1], sel.data[i+2])/255;
    for (let channel=0; channel<3; channel++) {
      const original = sel.data[i+channel];
      const colorized = color[['r','g','b'][channel]] * intensity;
      sel.data[i+channel] = Math.round(original*(1-factor)+colorized*factor);
    }
  }
  sel.source = new Uint8ClampedArray(sel.data); sel.sourceWidth = sel.width; sel.sourceHeight = sel.height;
  renderOverlay();
};
document.getElementById('dither-btn').onclick = () => {
  const sel = app.selection, settings = app.ditherSettings;
  if (!sel?.data || !app.selectionConfirmed) { showToast('Select an area first'); return; }
  if (!settings.colorRes && !settings.alphaRes) { showToast('Enable color or alpha resolution'); return; }
  const palette = settings.palette.length ? settings.palette : app.palette;
  for (let i=0; i<sel.data.length; i+=4) {
    const x = (i/4)%sel.width, y = Math.floor(i/4/sel.width);
    let threshold = 0;
    if (settings.method === 'random') threshold = Math.random()-.5;
    else if (settings.method === 'uniform') threshold = ((x+y)%2 ? .25 : -.25);
    else {
      const size = settings.mapSize;
      threshold = (((x%size)+((y%size)*size))*((settings.method === 'uniform-map') ? 1 : 3)%(size*size)+.5)/(size*size)-.5;
    }
    if (settings.colorRes) {
      const step = 255/(settings.colorLevels-1);
      const color = [0,1,2].map(c => Math.max(0,Math.min(255,Math.round((sel.data[i+c]+threshold*step)/step)*step)));
      if (palette.length) {
        const ranked = palette.map(c => ({c,d:(c.r-color[0])**2+(c.g-color[1])**2+(c.b-color[2])**2})).sort((a,b)=>a.d-b.d).slice(0,settings.closestColors);
        const index = settings.temperature ? Math.min(ranked.length-1,Math.floor((threshold+.5)*ranked.length*settings.temperature)) : 0;
        const c = ranked[index].c; sel.data.set([c.r,c.g,c.b],i);
      } else sel.data.set(color,i);
    }
    if (settings.alphaRes) {
      const step = 255/(settings.alphaLevels-1); sel.data[i+3] = Math.round((sel.data[i+3]+threshold*step)/step)*step;
    }
  }
  sel.source = new Uint8ClampedArray(sel.data); sel.sourceWidth = sel.width; sel.sourceHeight = sel.height;
  renderOverlay();
};

function saveAsDialog() {
  showDialog('Save project as', '<label for="document-name">File name</label><input class="dialog-input" id="document-name" placeholder="my-pixel-art" value="pixelforge">', () => {
    app.fileName = document.getElementById('document-name').value.trim().replace(/\.pix$/i,'') || 'pixelforge'; saveFile();
  });
  document.getElementById('document-name').value = app.fileName || 'pixelforge';
}
function exportAsDialog() {
  showDialog('Export PNG', '<label for="export-scale">Pixel scale (1–16×)</label><input type="number" class="dialog-input" id="export-scale" min="1" max="16" value="1"><p class="hint">Transparent background. Enlarged pixels stay crisp.</p>', () => exportPNG(Number(document.getElementById('export-scale').value)||1));
}
document.getElementById('save-as').onclick = saveAsDialog;
document.getElementById('export-as').onclick = exportAsDialog;

document.getElementById('fit-btn').insertAdjacentHTML('beforebegin','<button class="btn" id="resize-canvas-btn">Resize</button>');
function resizeDocument(width,height) {
  if (!Number.isInteger(width) || !Number.isInteger(height) || width<1 || height<1 || width>1024 || height>1024) return;
  pushHistory(); clearSelection(); app.shapeEditing = null;
  const oldWidth = app.canvasWidth, oldHeight = app.canvasHeight;
  for (const layer of app.layers) {
    const data = new Uint8ClampedArray(width*height*4);
    for (let y=0; y<Math.min(height,oldHeight); y++) {
      data.set(layer.data.slice(y*oldWidth*4,(y*oldWidth+Math.min(width,oldWidth))*4),y*width*4);
    }
    layer.data = data;
  }
  app.canvasWidth = width; app.canvasHeight = height; updateCanvas(); updateLayerList(); fitCanvasToView();
}
document.getElementById('resize-canvas-btn').onclick = () => {
  showDialog('Resize canvas', '<label for="resize-width">Width</label><input class="dialog-input" id="resize-width" type="number" min="1" max="1024"><label for="resize-height">Height</label><input class="dialog-input" id="resize-height" type="number" min="1" max="1024"><p class="hint">Keeps pixels at the top left. Smaller dimensions crop the artwork.</p>', () => {
    const w = Math.max(1,Math.min(1024,Math.round(Number(document.getElementById('resize-width').value)||app.canvasWidth)));
    const h = Math.max(1,Math.min(1024,Math.round(Number(document.getElementById('resize-height').value)||app.canvasHeight)));
    resizeDocument(w,h);
  });
  document.getElementById('resize-width').value = app.canvasWidth; document.getElementById('resize-height').value = app.canvasHeight;
};
document.getElementById('add-layer-btn').insertAdjacentHTML('afterend','<button class="btn" id="duplicate-layer-btn" style="width:auto;margin:0 8px 8px">Duplicate selected layer</button>');
document.getElementById('duplicate-layer-btn').onclick = () => {
  const source = getActiveLayer(); if (!source) return;
  const copy = addLayer(source.name + ' copy');
  if (!copy) return;
  copy.data = new Uint8ClampedArray(source.data); copy.visible = source.visible;
  updateLayerList(); render();
};
function selectLayer(id) {
  if (app.activeLayerId === id) id = null;
  if (app.activeLayerId !== id) {
    if (app.shapeEditing) commitShape();
    clearSelection();
    app.activeLayerId = id;
  }
  updateLayerList(); render();
}
const layerList = document.getElementById('layer-list');
layerList.addEventListener('click', event => {
  const row = event.target.closest('.layer-item');
  if (!row) { selectLayer(null); return; }
  const layer = app.layers.find(item => item.id === row.dataset.layerId);
  if (!layer) return;
  const action = event.target.closest('[data-layer-action]')?.dataset.layerAction;
  if (!action) { selectLayer(layer.id); return; }
  if (action === 'delete') { removeLayer(layer.id); return; }
  pushHistory();
  if (action === 'visibility') layer.visible = !layer.visible;
  if (action === 'lock') {
    if (app.activeLayerId === layer.id) { if (app.shapeEditing) commitShape(); clearSelection(); }
    layer.locked = !layer.locked;
  }
  updateLayerList(); render();
});
layerList.addEventListener('keydown', event => {
  if (event.target.closest('button') || !['Enter', ' '].includes(event.key)) return;
  const row = event.target.closest('.layer-item');
  if (row) { event.preventDefault(); selectLayer(row.dataset.layerId); }
});
layerList.addEventListener('dblclick', event => {
  if (!event.target.closest('.layer-name')) return;
  const id = event.target.closest('.layer-item').dataset.layerId;
  const layer = app.layers.find(item => item.id === id);
  if (!layer) return;
  app.activeLayerId = id; updateLayerList();
  showDialog('Rename layer', '<input class="dialog-input" id="layer-new-name" maxlength="80" aria-label="Layer name">', () => {
    const name = document.getElementById('layer-new-name').value.trim();
    if (name) { pushHistory(); layer.name = name; updateLayerList(); }
  });
  document.getElementById('layer-new-name').value = layer.name;
});
// Replace the old document-level drag emulation with standard accessible ranges.
document.querySelectorAll('.unlimited-slider').forEach(slider => {
  slider.onmousedown = null;
  slider.min = slider.dataset.min;
  slider.max = slider.dataset.max;
});
document.onmousemove = null; document.onmouseup = null;
// Selected source pixels are hidden while the floating preview is moved.
const renderBeforeSelectionPreview = render;
render = function() {
  const sel = app.selection;
  const layer = sel?.origin && app.selectionConfirmed ? app.layers.find(l=>l.id===sel.layerId) : null;
  if (!layer) { renderBeforeSelectionPreview(); return; }
  const saved = layer.data; layer.data = new Uint8ClampedArray(saved);
  const c = app.color2, o = sel.origin;
  for (let y=o.y;y<o.y+o.height;y++) for (let x=o.x;x<o.x+o.width;x++) layer.data.set([c.r,c.g,c.b,c.a],(y*app.canvasWidth+x)*4);
  try { renderBeforeSelectionPreview(); } finally { layer.data = saved; }
};

// Keep each native picker beside the active-color controls, initialized in place.
const nativeSlots = document.createElement('div'); nativeSlots.className = 'native-color-slots';
document.querySelector('.color-targets').after(nativeSlots);
for (const target of ['color1','color2']) {
  const section = document.getElementById(target+'-picker').closest('.menu-section');
  section.querySelector('.menu-section-title').classList.add('hidden');
  section.querySelector('.slider-group').classList.add('hidden');
  nativeSlots.append(section);
}

const canvasArea = document.getElementById('canvas-area');
canvasArea.onwheel = e => {
  e.preventDefault();
  if (e.ctrlKey || e.metaKey) {
    const rect = canvasArea.getBoundingClientRect();
    const mx = (e.clientX-rect.left)*canvasArea.clientWidth/rect.width;
    const my = (e.clientY-rect.top)*canvasArea.clientHeight/rect.height;
    const x = (mx-app.panX)/app.zoom, y = (my-app.panY)/app.zoom;
    app.zoom = Math.max(.1,Math.min(64,app.zoom*(e.deltaY>0?.9:1.1)));
    app.panX = mx-x*app.zoom; app.panY = my-y*app.zoom;
  } else if (e.shiftKey) app.panX += e.deltaY;
  else { app.panX += e.deltaX; app.panY += e.deltaY; }
  updateTransform();
};
const originalMouseLeave = canvasArea.onmouseleave;
canvasArea.onmouseleave = e => { canvasArea.onmouseup(e); originalMouseLeave(e); };
let canvasGestureActive = false;
canvasArea.addEventListener('mousedown', event => {
  if (!event.target.closest('.workspace-toolbar')) canvasGestureActive = true;
});
window.addEventListener('mouseup', event => {
  if (canvasGestureActive && !canvasArea.contains(event.target)) canvasArea.onmouseup(event);
  canvasGestureActive = false;
});

// Swatches use an editor-owned dialog. No screen-capture picker is involved.
const colorDialog = document.createElement('dialog');
colorDialog.id = 'canvas-color-dialog';
colorDialog.className = 'color-dialog';
colorDialog.setAttribute('aria-labelledby', 'canvas-color-dialog-title');
colorDialog.innerHTML = '<h3 id="canvas-color-dialog-title">Paint color</h3><button class="btn" id="dialog-pick-canvas">Pick from canvas</button><p class="hint">The eyedropper reads artwork data, unaffected by display tint.</p><div id="dialog-color-controls"></div><div class="btn-row"><button class="btn" id="dialog-color-cancel">Cancel</button><button class="btn active" id="dialog-color-apply">Use color</button></div>';
document.body.append(colorDialog);
const colorControls = document.querySelector('.color-controls');
const colorControlsHome = document.createComment('Color controls return here after the dialog closes');
colorControls.before(colorControlsHome);
let dialogColorTarget = 'color1', dialogOpeningColor = null, pendingCanvasColorTarget = null;
function openCanvasColorDialog(target) {
  if (colorDialog.open) return;
  dialogColorTarget = target; dialogOpeningColor = {...app[target]};
  app.activeColor = target;
  updateColorPickers();
  document.getElementById('dialog-color-controls').append(colorControls);
  document.getElementById('canvas-color-dialog-title').textContent = target === 'color1' ? 'Primary color' : 'Secondary color';
  colorDialog.returnValue = '';
  colorDialog.showModal();
}
colorDialog.addEventListener('cancel', e => { e.preventDefault(); colorDialog.close('cancel'); });
colorDialog.addEventListener('close', () => {
  colorControlsHome.after(colorControls);
  if (colorDialog.returnValue === 'cancel') { app[dialogColorTarget] = dialogOpeningColor; updateColorPickers(); }
  if (colorDialog.returnValue === 'pick') {
    pendingCanvasColorTarget = dialogColorTarget;
    eyedropperButton.click();
    document.querySelector('[data-menu="tools"]').click();
  }
});
document.getElementById('dialog-color-cancel').onclick = () => colorDialog.close('cancel');
document.getElementById('dialog-color-apply').onclick = () => colorDialog.close('apply');
document.getElementById('dialog-pick-canvas').onclick = () => colorDialog.close('pick');
for (const target of ['color1','color2']) {
  const input = document.getElementById(target+'-picker');
  input.type = 'text'; input.classList.add('hidden'); input.tabIndex = -1;
  const button = document.createElement('button');
  button.className = 'color-picker-input color-dialog-open';
  button.id = target+'-open';
  button.setAttribute('aria-label', target === 'color1' ? 'Edit primary color' : 'Edit secondary color');
  button.title = 'Edit color / pick from canvas';
  button.onclick = () => openCanvasColorDialog(target);
  input.after(button);
}

document.getElementById('undo-btn').insertAdjacentHTML('beforebegin', '<button class="btn" id="new-image-btn">New Image</button>');
document.getElementById('new-image-btn').onclick = newFile;
