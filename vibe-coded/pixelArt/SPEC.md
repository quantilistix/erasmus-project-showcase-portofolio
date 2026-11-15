# Pixel Art Editor - Specification

## Project Overview
- **Project Name**: PixelForge
- **Type**: Interactive pixel art editor web application
- **Core Functionality**: Full-featured pixel art creation with layers, transforms, shapes, and advanced color tools
- **Target Users**: Pixel artists, game developers, digital artists

---

## Visual & UI Specification

### Layout
```
+------------------+------------------------+------------------+
|   Left Panel     |                      |   Right Panel    |
|   (Tools/Menu)   |       Canvas         |   (Layers)       |
|   ~200px fixed   |     (centered)       |   ~200px fixed   |
|                  |                      |                  |
+------------------+------------------------+------------------+
```

### Color Scheme
- **Background**: #1a1a1a (dark gray)
- **Panel Background**: #252525 (slightly lighter)
- **Panel Border**: #3a3a3a
- **Button Normal**: #333333
- **Button Hover**: #444444
- **Button Active**: #555555
- **Text Primary**: #e0e0e0
- **Text Secondary**: #888888
- **Accent**: #4a9eff (blue)
- **Canvas Background**: Checkered pattern (transparency indicator)

### Typography
- **Font Family**: 'JetBrains Mono', 'Fira Code', monospace
- **Panel Title**: 11px, uppercase, letter-spacing: 1px
- **Button Text**: 12px
- **Input Labels**: 10px

---

## Canvas Specification

### Properties
- **Initial Size**: 64x64 pixels
- **Min Size**: 8x8 pixels
- **Max Size**: 1024x1024 pixels
- **Background**: Checkered pattern (white #fff / gray #ccc)

### Resize Handles
- 8 handles: 4 corners + 4 edge midpoints
- Handle size: 10px × 10px
- Handle color: #4a9eff (accent blue)
- Handle visibility: Always visible
- Cursor changes: nwse-resize, nesw-resize, ew-resize, ns-resize

### Zoom & Pan
- **Zoom**: Ctrl + mousewheel (unlimited scale)
- **Zoom Range**: 1x to 64x (1x = 1 pixel per canvas pixel)
- **Pan Vertical**: Mousewheel
- **Pan Horizontal**: Shift + Mousewheel
- **Initial Zoom**: Fit canvas to viewport

---

## Layer System

### Properties
- All layers have identical canvas resolution
- Max 32 layers
- Layer controls: drag reorder, delete, duplicate, lock, hide, select

### Layer Object Structure
```javascript
{
  id: string,
  name: string,
  visible: boolean,
  locked: boolean,
  selected: boolean,
  data: Uint8ClampedArray // RGBA data
}
```

### Right Panel (Layers)
- Layer list with thumbnails (40x40px)
- Visibility toggle (eye icon)
- Lock toggle (lock icon)
- Selection indicator (highlight border)
- Drag handle for reordering
- Delete button
- Add layer button (+)
- Layer opacity display

---

## Left Panel - Menu System

### Menu Navigation
- Horizontal button row at top of left panel
- Menus: Tools | Colors | File I/O
- Active menu indicated by highlighted button

---

## TOOLS MENU

### Tool Buttons
1. **Select** (S) - Selection rectangle
2. **Pixel Draw** (B) - Brush tool
3. **Eraser** (E) - Eraser tool
4. **Shape** (U) - Shape drawing

### SELECT TOOL

#### Selection Rectangle
- Drag to create selection
- 8 resize handles (same as canvas)
- Move cursor on drag
- Color: #4a9eff border, semi-transparent fill

#### Controls (shown in panel when selection active)
- **Done!** button - Confirm selection
- **Cancel** button - Cancel selection

#### After Confirmation - Transformations
- **Move**: Drag selection, empty space filled with Color2
  - Sliders: X offset, Y offset (unlimited range)
- **Resize**: Drag corners/edges
  - Sliders: Width, Height (unlimited range)
- **Rotate**: Rotation handle above selection
  - Slider: Angle (0-360°, unlimited via dragging)
  - Uses skew technique for pixel-perfect rotation
- **Skew**: Two sliders for X and Y skew
  - Direct drag interaction, no canvas handle

#### Smoothing Toggle
- **ON**: 
  - Upscaling: Bilinear interpolation
  - Downscaling: Averaging pixels
  - Rotating: Skewing technique with interpolation
  - Skewing: Interpolated
- **OFF**:
  - All operations use nearest-neighbor

#### Clipboard
- **Copy** (Ctrl+C) - Copy selection to clipboard
- **Paste** (Ctrl+V) - Paste at cursor position

#### Dithering Section
- **Color Resolution Toggle**: Enable/disable
- **Color Resolution**: Slider 1-256 (when enabled)
- **Alpha Resolution Toggle**: Enable/disable  
- **Alpha Resolution**: Slider 1-256 (when enabled)
- **Palette Size**: 2-256 colors (when color resolution enabled)
- **Palette Colors**: Editable color list
- **Closest Colors**: 1-10 (default 1 = no dithering)
- **Temperature**: 0.0-1.0 slider (0 = deterministic, 1 = random)
- **Dithering Method**: 
  - Random
  - Uniform
  - Map (with size parameter 2-8)
  - Uniform Map

#### Colorize Section
- **Color Picker**: Target RGBA color
- Multiply the target RGB color by the source pixel's intensity (HSV value: max(R, G, B) / 255), preserving alpha.
- **Interpolation**: 0-100% slider (default 100%); interpolate between the original pixel and the intensity-scaled target color.

#### Deselection
- Click elsewhere on canvas
- Press ESC
- Select another tool

### PIXEL DRAW TOOL

#### Brush Settings
- **Size**: 1-32 pixels (slider)
- **Shape**: 
  - Square
  - Circle
  - Diamond

#### Drawing
- Left click: Paint with Color1
- Right click: Paint with Color2

### ERASER TOOL
- Same as Pixel Draw
- Left click: Paint with Color2
- Right click: Paint with Color1

### SHAPE TOOL

#### Shape Types
- **Rectangle** [] - Rectangular outline/fill
- **Oval** () - Elliptical outline/fill
- **Diamond** <> - Rotated square outline/fill

#### Options
- **Fill Toggle**: Fill interior with Color2
- **Outline Thickness**: 1-8 pixels

#### Behavior
- Drag to define bounding box
- Shows preview during drag
- After release: Can move/resize/rotate/skew (like Select tool)
- On deselection: Commits to canvas

---

## COLORS MENU

### Color Swatches
- **Color1**: Primary painting/outline color
- **Color2**: Secondary erase/fill color
- Size: 60x60px each

### Swap Button
- Swaps Color1 ↔ Color2

### Color Input Modes
- **RGBA**: 4 sliders (0-255 each)
- **HSVA**: Hue (0-360°), Saturation (0-100%), Value (0-100%), Alpha (0-100%)

### Gradient Picker
- X-axis: Hue (0-360°)
- Y-axis: Saturation (0-100%)
- Slider: Value (0-100%)
- Click to select color

### Alpha Toggle
- Blend mode vs Overwrite mode

### Color Picker Button
- Opens native color picker

---

## FILE I/O MENU

### Buttons
- **New File** - Create new canvas
- **Save File** (Ctrl+S) - Save to localStorage/JSON
- **Save As** (Ctrl+Alt+S) - Save with name prompt
- **Load File** (Ctrl+L) - Load from storage
- **Export File** (Ctrl+E) - Export as PNG
- **Export As** (Ctrl+Alt+E) - Export with size options
- **Import File** (Ctrl+I) - Import image file

### File Format (Internal)
```javascript
{
  version: "1.0",
  canvasWidth: number,
  canvasHeight: number,
  zoom: number,
  panX: number,
  panY: number,
  layers: [...],
  color1: {r,g,b,a},
  color2: {r,g,b,a},
  activeTool: string,
  activeLayer: number
}
```

---

## Interaction Specification

### Keyboard Shortcuts
| Action | Shortcut |
|--------|----------|
| New File | Ctrl+N |
| Open File | Ctrl+O (mapped to Load) |
| Save File | Ctrl+S |
| Save As | Ctrl+Alt+S |
| Load File | Ctrl+L |
| Export | Ctrl+E |
| Export As | Ctrl+Alt+E |
| Import | Ctrl+I |
| Copy | Ctrl+C |
| Paste | Ctrl+V |
| Undo | Ctrl+Z |
| Redo | Ctrl+Y |
| Select Tool | S |
| Brush Tool | B |
| Eraser | E |
| Shape Tool | U |

### Mouse Interactions
- **Left Click**: Primary action (paint, select, etc.)
- **Right Click**: Secondary action (erase, alternate paint)
- **Ctrl+Wheel**: Zoom
- **Wheel**: Vertical pan
- **Shift+Wheel**: Horizontal pan
- **Drag Handles**: Resize/move elements

### Event Handling Priority
1. Tool-specific handlers
2. Canvas resize handlers
3. Panel UI handlers
4. Keyboard shortcuts

---

## Technical Implementation

### Canvas Rendering
- Use OffscreenCanvas for layer compositing
- Main canvas renders checkerboard + layers + UI
- Separate overlay canvas for selection/shape previews

### Data Structures
```javascript
// Layer
{
  id: string,
  name: string,
  visible: boolean,
  locked: boolean,
  selected: boolean,
  opacity: number,
  data: ImageData
}

// Selection
{
  x: number,
  y: number,
  width: number,
  height: number,
  pixels: ImageData | null,
  confirmed: boolean
}

// Shape Preview
{
  type: 'rect' | 'oval' | 'diamond',
  x: number,
  y: number,
  width: number,
  height: number,
  filled: boolean,
  outlineWidth: number
}
```

### Image Processing
- All operations work on ImageData arrays
- Interpolation functions for smooth scaling
- Skew math for rotation

---

## Acceptance Criteria

1. ✅ Canvas displays centered with checkered background
2. ✅ Canvas can be resized via 8 draggable handles
3. ✅ Zoom works with Ctrl+Wheel (unlimited)
4. ✅ Pan works with Wheel (vertical) and Shift+Wheel (horizontal)
5. ✅ Multiple layers can be added/removed/reordered
6. ✅ Layer visibility and lock states work
7. ✅ Pixel draw tool paints with brush preview
8. ✅ Brush shapes (square, circle, diamond) work
9. ✅ Eraser tool reverses color assignment
10. ✅ Shape tool creates rectangles, ovals, diamonds
11. ✅ Select tool creates selection rectangle
12. ✅ Selection can be moved, resized, rotated, skewed
13. ✅ Smoothing toggle affects transformations
14. ✅ Copy/Paste works for selection
15. ✅ Dithering controls are functional
16. ✅ Color pickers work (RGBA, HSVA, gradient)
17. ✅ File save/load preserves all state
18. ✅ Export creates valid PNG
19. ✅ Import loads external images
20. ✅ All keyboard shortcuts work
