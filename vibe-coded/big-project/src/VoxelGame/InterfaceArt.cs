using GlmSharp;

using MYT.Kit.Gui;

namespace VoxelGame;


/// <summary>
/// The icon a status bar is drawn with, whole, half and none full: <c>2</c>, <c>1</c> and
/// <c>0</c> before its name among the images.
/// </summary>
internal readonly record struct StatusIcon(GuiImage Full, GuiImage Half, GuiImage Empty);

/// <summary>
/// What the interface draws with, packed into one <see cref="GuiAtlas"/> for the renderer: the
/// font, the dirt the menus are tiled with, the title, the inventory's slot, tabs and scroll
/// knob, the status bars' icons, and an icon per item: a block as a cube seen from above a
/// corner, as Minecraft's inventory shows blocks, any other item as its image.
/// </summary>
internal sealed class InterfaceArt
{
    // A block's icon: its size (pixels), and the corners of the cube in it: the top face's top,
    // right, front and left corners, and the drop of its sides.
    private const int IconSize = 32;
    private static readonly vec2 CubeTop = new(16f, 1f);
    private static readonly vec2 CubeRight = new(29f, 8.5f);
    private static readonly vec2 CubeFront = new(16f, 16f);
    private static readonly vec2 CubeLeft = new(3f, 8.5f);
    private static readonly vec2 CubeDrop = new(0f, 15f);

    /// <summary>
    /// The characters of bitmapfont.png, the C64 character set: a 32 × 8 grid of 18-pixel
    /// cells, each holding its glyph, drawn at twice its size, a pixel in from the corner.
    /// Only the first four rows are letters, digits and punctuation.
    /// </summary>
    private static IEnumerable<(char Character, int Cell)> FontCharacters()
    {
        yield return ('@', 0);
        for (char c = 'a'; c <= 'z'; c++)
            yield return (c, c - 'a' + 1);
        yield return ('[', 27);
        yield return (']', 29);
        for (char c = ' '; c <= '?'; c++)
            yield return (c, c);
        for (char c = 'A'; c <= 'Z'; c++)
            yield return (c, c - 'A' + 65);
        yield return ('_', 100);
    }

    /// <summary>The C64 sheet is dark ink on a light ground; the interface draws white ink, tinted.</summary>
    private static GuiBitmap InkToAlpha(GuiBitmap sheet)
    {
        GuiBitmap ink = new(sheet.Width, sheet.Height);
        for (int y = 0; y < sheet.Height; y++)
        for (int x = 0; x < sheet.Width; x++)
            ink[x, y] = sheet[x, y].R < 128 ? GuiColor.White : GuiColor.Transparent;

        return ink;
    }

    /// <summary>
    /// A block as a cube seen from above one of its corners: its top face, lit fully, and two of
    /// its sides, the left lit less and the right less again, as if the light came from above
    /// and behind the left.
    /// </summary>
    private static GuiBitmap CubeIcon(GuiBitmap top, GuiBitmap left, GuiBitmap right)
    {
        (vec2 Origin, vec2 Across, vec2 Down, GuiBitmap Texture, float Light)[] faces =
        [
            (CubeTop, CubeRight - CubeTop, CubeLeft - CubeTop, top, 1.0f),
            (CubeLeft, CubeFront - CubeLeft, CubeDrop, left, 0.8f),
            (CubeFront, CubeRight - CubeFront, CubeDrop, right, 0.6f)
        ];

        GuiBitmap icon = new(IconSize, IconSize);
        for (int y = 0; y < IconSize; y++)
        for (int x = 0; x < IconSize; x++)
        {
            vec2 pixel = new(x + 0.5f, y + 0.5f);
            foreach ((vec2 origin, vec2 across, vec2 down, GuiBitmap texture, float light) in faces)
            {
                // Where on the face the pixel is, from 0 to 1 across and down it.
                vec2 offset = pixel - origin;
                float determinant = across.x * down.y - across.y * down.x;
                float u = (offset.x * down.y - offset.y * down.x) / determinant;
                float v = (across.x * offset.y - across.y * offset.x) / determinant;
                if (u < 0f || v < 0f || u >= 1f || v >= 1f)
                    continue;

                GuiColor texel = texture[(int)(u * texture.Width), (int)(v * texture.Height)];
                icon[x, y] = texel.Scaled(light);
                break;
            }
        }

        return icon;
    }

    private static GuiBitmap ShapeIcon(BlockType type, GuiBitmap texture)
    {
        GuiBitmap icon = new(IconSize, IconSize);
        MYT.Kit.Vulkan.Grid3D<Voxel> grid = new();
        grid[ivec3.Zero] = new Voxel(type, 0);
        dvec3 direction = new dvec3(-1, -1, -1).Normalized;
        for (int y = 0; y < IconSize; y++)
        for (int x = 0; x < IconSize; x++)
        {
            double across = (x + 0.5 - 16) / 13;
            double down = (y + 0.5 - 16) / 7.5;
            dvec3 origin = new((down + 6 + across) / 2, (down + 6 - across) / 2, 3);
            if (!BlockShapes.Raycast(grid, origin, direction, 10, out var hit)) continue;
            dvec3 p = origin + direction * hit.Distance;
            double u = hit.Normal.z != 0 ? p.x : hit.Normal.x != 0 ? p.y : 1 - p.x;
            double v = hit.Normal.z != 0 ? 1 - p.y : 1 - p.z;
            int tx = Math.Clamp((int)(u * texture.Width), 0, texture.Width - 1);
            int ty = Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1);
            icon[x, y] = texture[tx, ty].Scaled(hit.Normal.z != 0 ? 1f : hit.Normal.y != 0 ? 0.8f : 0.6f);
        }
        return icon;
    }

    public InterfaceArt(string imageDirectory, ItemSprites sprites)
    {
        Dictionary<string, GuiBitmap> loaded = [];
        GuiBitmap Load(string name)
        {
            if (!loaded.TryGetValue(name, out GuiBitmap? bitmap))
                loaded[name] = bitmap = GuiBitmap.Load(Path.Combine(imageDirectory, name + ".png"));

            return bitmap;
        }

        StatusIcon Status(string name)
        {
            return new StatusIcon(Atlas.Add(Load("2" + name)), Atlas.Add(Load("1" + name)), Atlas.Add(Load("0" + name)));
        }

        Atlas = new GuiAtlas(512, 512);
        GuiBitmap sheet = InkToAlpha(Load("bitmapfont"));
        GuiFont font = GuiFont.FromGrid(Atlas, sheet, new GuiFontGrid(18, 18, 16, 16, Inset: 1, PixelSize: 2), FontCharacters(), spacing: 1);
        Style = new GuiStyle(font)
        {
            ListBackground = GuiColor.Transparent,
            Button = new
            (
                GuiColor.FromRgb(0x6A9FD4),
                GuiColor.FromRgb(0x9CC4EC),
                GuiColor.FromRgb(0x41709F),
                GuiColor.Black
            ),
            ButtonHovered = new
            (
                GuiColor.FromRgb(0x86B9EA),
                GuiColor.FromRgb(0xBADAF8),
                GuiColor.FromRgb(0x5A8BC0),
                GuiColor.Black
            )
        };
        Dirt = Atlas.Add(Load("menu_tiling"));
        Title = Atlas.Add(Load("title"));
        Slot = Atlas.Add(Load("item_slot"));
        ScrollKnob = Atlas.Add(Load("scroll_nob"));
        Health = Status("hp");
        Hunger = Status("ep");
        Thirst = Status("wp");
        Defense = Status("dp");

        for (int type = 1; type < Blocks.Count; type++)
        {
            BlockMaterial material = Blocks.Get((BlockType)type);
            _icons[Items.Of((BlockType)type)] = Atlas.Add(BlockShapes.HasShape((BlockType)type) ? ShapeIcon((BlockType)type, Load(BlockShapes.IsDoor((BlockType)type) ? material.North : material.Top))
                : CubeIcon(Load(material.Top), Load(material.North), Load(material.East)));
        }

        foreach (ItemType type in Items.NonBlockTypes)
            _icons[type] = Atlas.Add(new GuiBitmap(ItemSprites.Size, ItemSprites.Size, (byte[])sprites.Pixels(type).Clone()));
    }

    private readonly Dictionary<ItemType, GuiImage> _icons = [];

    public GuiAtlas Atlas { get; }
    public GuiStyle Style { get; }
    public GuiImage Dirt { get; }
    public GuiImage Title { get; }

    /// <summary>An inventory slot, 18 units a side, sunk into the panel.</summary>
    public GuiImage Slot { get; }

    /// <summary>The knob of a scrolling list, 8 × 12.</summary>
    public GuiImage ScrollKnob { get; }

    /// <summary>The status bars' icons, 12 × 12.</summary>
    public StatusIcon Health { get; }
    public StatusIcon Hunger { get; }
    public StatusIcon Thirst { get; }
    public StatusIcon Defense { get; }

    /// <summary>An item as the interface shows it.</summary>
    public GuiImage Icon(ItemType type)
    {
        return _icons[type];
    }

    /// <summary>A block as the interface shows it, as a cube.</summary>
    public GuiImage Icon(BlockType type)
    {
        return _icons[Items.Of(type)];
    }
}
