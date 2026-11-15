# MYT GUI

`MYT.Kit.Gui` is an immediate-mode interface for games on MYT: buttons, sliders, text fields, scrolling lists, images, tiled backgrounds and text. It draws into a plain list of vertices, so any renderer can show it in one draw call. The voxel game's menus and HUD use it (`src/VoxelGame/App.cs`, `Game.cs`), as do the Brain Editor and neural maze dashboard.

## How it works

Every frame, between `Begin` and `End`, the game calls a method for each element it shows. The method draws the element and returns what the player did to it:

```csharp
gui.Begin(input, new vec2(width, height), scale);
if (gui.Button(new GuiRect(x, y, 200, 20), "Singleplayer"))
    OpenWorlds();
gui.Slider("render", row, ref renderDistance, 4, 32, $"Render Distance: {renderDistance} chunks");
gui.TextField("name", field, ref worldName, maxLength: 32);
gui.End();
// The game's renderer draws gui.DrawList.Vertices.
```

The game keeps its own state and lays the screen out again each frame. The context keeps only what elements need while they are in use: which element the pointer drags, which one takes the keys, the caret and each list's scroll. Elements that need this state take a string id. The id must stay the same from frame to frame and be unique on the screen.

Layout is in **units**, `Scale` pixels each. At a whole scale, a pixel-art font or image drawn at its own size, or a whole multiple of it, keeps its pixels square and sharp. `GuiContext.FitScale(pixels, minimum)` picks the largest whole scale that leaves the screen at least `minimum` units, as Minecraft's automatic GUI scale does.

## Parts

| Type | What |
|---|---|
| `GuiContext` | The frame (`Begin`, `End`), focus, drawing (`Rect`, `Gradient`, `Outline`, `Image`, `Tile`, `Bevel`, `Text`, `Label`, `Wrap`, clips) and the elements. |
| `GuiStyle` | Font, colors and sizes. The defaults look like Minecraft's classic menus. |
| `GuiAtlas` | The one texture: images packed in rows, each with its edge pixels repeated around it, plus a white pixel for flat colors. |
| `GuiBitmap` | RGBA pixels to add to the atlas; `Load` reads PNG and other formats. |
| `GuiFont`, `GuiFontGrid` | A bitmap font cut from a grid sheet, fixed-width or proportional (glyphs cut to their ink). |
| `GuiDrawList`, `GuiVertex` | The output: triangles in pixels, already clipped and rounded to whole pixels. |
| `GuiInput`, `GuiKeys` | What the game hands in each frame: the pointer, the primary and secondary buttons, SHIFT, the wheel, typed text, editing keys and a clock. |
| `GuiRect`, `GuiColor` | Rectangles (with `Column` for rows of buttons) and sRGB colors. |

## Elements

- **Button**: clicks on the press, as in Minecraft. A disabled button shows dark and never clicks.
- **Slider**: a dark track with a handle and its label across it. Dragging sets it, even off the track. Once clicked, LEFT and RIGHT step it. It works on floats with an optional step, or on whole numbers.
- **Text field**: clicking it gives it the keys. Typing, BACKSPACE, DELETE, LEFT, RIGHT, HOME and END edit it. TAB moves to the next field, and from the last back to the first. A click anywhere else takes the keys away. The field scrolls to keep the caret in view, can filter characters and has a length limit.
- **List**: `BeginList`, then a `ListRow` and contents for each visible row, then `EndList`. Rows are cut to the list. The wheel scrolls it, and a scrollbar appears when the rows don't fit. `ListRow` returns `Single` or `Double` for clicks.
- **The game's own elements**: `IsHovered` tells whether the pointer is over a rectangle, `MousePressed` and `SecondaryPressed` whether a button went down this frame, `Input.Shift` whether SHIFT is held, and `Drag(id, rect)` whether the pointer drags something grabbed on that rectangle, as a scrollbar's knob, until the button is let go. The voxel game's inventory screen is built from these (`src/VoxelGame/InventoryScreen.cs`).

## Setting it up in a game

1. Make a `GuiAtlas`, add a font (`GuiFont.FromGrid`) and the images the interface draws, and make a `GuiStyle` from the font. The atlas must hold every image before the game uploads it, and again whenever `Version` changes.
2. Upload `atlas.Pixels` as an `R8G8B8A8_SRGB` texture, sampled nearest and clamped to its edge.
3. Each frame, copy `DrawList.Vertices` into a vertex buffer and draw it as a triangle list. Use alpha blending, no depth test and no culling. The vertex is 20 bytes: position `R32G32_SFLOAT` at 0, uv `R32G32_SFLOAT` at 8, color `R8G8B8A8_UNORM` at 16.
4. Draw with `assets/shaders/gui/gui.vert` and `gui.frag`. They need one combined image sampler at set 0, binding 0, and the screen size in pixels as a `vec2` push constant. They are written for an sRGB framebuffer: vertex colors are sRGB and are turned linear in the vertex shader.

On an sRGB framebuffer, blending happens in linear light. A translucent dark veil therefore dims much less than the same alpha does in Minecraft, which blends in sRGB. Use a higher alpha for overlays.

Tests are in `src/VoxelTests/GuiTests.cs` (`gui-*`).

## Native neural tools

The editor and maze use a shared dark theme and atlas in `src/ArcadeLab/Viewer.Gui.cs`. `GuiDefaultFont.Create(atlas)` supplies an ASCII bitmap font without external assets; lowercase glyphs remain distinct when editing JSON and paths. `GuiRasterizer.Draw` samples GUI quads into horizontal colored spans, for the existing composed framebuffer and native rectangle fallback. It supports nearest sampling, clipping, textures, vertex tint and vertical gradients. Its destination blends the supplied straight alpha. The framebuffer adapter blends over the existing scene; the rectangle fallback paints spans opaquely.

The maze uses GUI buttons for AI/manual control, pause/resume and episode restart, alongside the existing keyboard shortcuts. Its observation strips continue to show the actual sensor inputs; the shaded top-view map is an observer view. `Viewer.CaptureMaze` and `Viewer.CaptureCanvas` render without a window for checks.

Neural weight visualization uses a separate `MYT.Kit.Graph` Vulkan overlay: line-list weights with a depth buffer, then depth-tested neuron triangles and a separate text/handle overlay. Graph weights do not expand into per-pixel GUI quads. GUI flat fills and gradients take a row-span fast path in `GuiRasterizer`; glyphs retain nearest texture sampling. See [Neural.md](Neural.md) for graph navigation and GPU checks.
