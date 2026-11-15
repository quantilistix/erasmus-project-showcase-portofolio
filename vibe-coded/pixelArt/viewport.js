'use strict';

// Keep document and overlay canvases at artwork resolution, but present them
// through a viewport-sized canvas. Enlarging a tiny canvas with a CSS transform
// gives the webview compositor another opportunity to resample its texture.
const viewportCanvas = document.createElement('canvas');
viewportCanvas.id = 'artwork-viewport';
viewportCanvas.setAttribute('aria-label', 'Pixel art canvas');
canvasArea.prepend(viewportCanvas);
const viewportContext = viewportCanvas.getContext('2d');

function artworkViewBounds() {
  // This is the single projection used by both the raster renderer and input.
  return mainCanvas.getBoundingClientRect();
}

function renderViewport() {
  const viewport = viewportCanvas.getBoundingClientRect();
  if (!viewport.width || !viewport.height) return;
  const ratio = window.devicePixelRatio || 1;
  const width = Math.max(1, Math.round(viewport.width * ratio));
  const height = Math.max(1, Math.round(viewport.height * ratio));
  if (viewportCanvas.width !== width) viewportCanvas.width = width;
  if (viewportCanvas.height !== height) viewportCanvas.height = height;
  const scaleX = width / viewport.width, scaleY = height / viewport.height;
  const bounds = artworkViewBounds();
  const left = (bounds.left - viewport.left) * scaleX;
  const top = (bounds.top - viewport.top) * scaleY;
  const tileWidth = bounds.width * scaleX, tileHeight = bounds.height * scaleY;
  viewportContext.clearRect(0, 0, width, height);
  viewportContext.imageSmoothingEnabled = false;
  const radius = app.tileView ? 1 : 0;
  // One draw maps the complete source rectangle to its complete destination
  // rectangle. There are no half-source-pixel insets or CSS-scaled art textures.
  for (let y = -radius; y <= radius; y++) {
    for (let x = -radius; x <= radius; x++) {
      viewportContext.drawImage(mainCanvas, 0, 0, app.canvasWidth, app.canvasHeight,
        left + x * tileWidth, top + y * tileHeight, tileWidth, tileHeight);
    }
  }
  viewportContext.drawImage(overlayCanvas, 0, 0, app.canvasWidth, app.canvasHeight,
    left, top, tileWidth, tileHeight);
}

getCanvasCoords = function(event) {
  const bounds = artworkViewBounds();
  return {
    x: Math.floor((event.clientX - bounds.left) / bounds.width * app.canvasWidth),
    y: Math.floor((event.clientY - bounds.top) / bounds.height * app.canvasHeight)
  };
};

// Coalesce render/overlay/transform changes into the next display frame. Input
// reads the same up-to-date projection even before that frame is presented.
let viewportFrame = null;
function requestViewportRender() {
  if (viewportFrame !== null) return;
  viewportFrame = requestAnimationFrame(() => {
    viewportFrame = null;
    renderViewport();
  });
}
const updateSourceTransform = updateTransform;
updateTransform = function() { updateSourceTransform(); requestViewportRender(); };
const renderSourceOverlay = renderOverlay;
renderOverlay = function() { renderSourceOverlay(); requestViewportRender(); };
const renderSourceArtwork = render;
render = function() { renderSourceArtwork(); requestViewportRender(); };
new ResizeObserver(requestViewportRender).observe(canvasArea);
renderViewport();
