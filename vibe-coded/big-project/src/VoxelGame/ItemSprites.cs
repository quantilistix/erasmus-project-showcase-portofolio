using StbImageSharp;

namespace VoxelGame;


/// <summary>
/// The images of the items that are not blocks (<see cref="ItemInfo.Sprite"/>), read once from
/// the images directory: 16 × 16, transparent around the item. Their models, drawn and
/// collided with, are cut from them a pixel thick (<see cref="ItemMesher"/>).
/// </summary>
internal sealed class ItemSprites
{
    public const int Size = 16;

    /// <exception cref="InvalidDataException">An image is not 16 × 16.</exception>
    public ItemSprites(string imageDirectory)
    {
        foreach (ItemType type in Items.NonBlockTypes)
        {
            string path = Path.Combine(imageDirectory, Items.Get(type).Sprite + ".png");
            using FileStream stream = File.OpenRead(path);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image.Width != Size || image.Height != Size)
                throw new InvalidDataException($"{path} is {image.Width}×{image.Height}; item images must be {Size}×{Size}");

            _images[type] = image.Data;
        }
    }

    private readonly Dictionary<ItemType, byte[]> _images = [];

    /// <summary>An item's image, RGBA, a row at a time from the top.</summary>
    public byte[] Pixels(ItemType type)
    {
        return _images[type];
    }

    /// <summary>Which pixels of an item's image show, by x then y from the top-left: those not wholly transparent.</summary>
    public bool[,] Opaque(ItemType type)
    {
        byte[] pixels = _images[type];
        bool[,] opaque = new bool[Size, Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
            opaque[x, y] = pixels[(y * Size + x) * 4 + 3] > 0;

        return opaque;
    }
}
