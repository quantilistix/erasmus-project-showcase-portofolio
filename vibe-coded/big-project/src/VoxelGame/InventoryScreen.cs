using GlmSharp;

using MYT.Kit.Gui;

using Silk.NET.GLFW;

namespace VoxelGame;


/// <summary>Creative catalog beside the player inventory, with opened station recipes above it.</summary>
internal sealed class InventoryScreen
{
    private const float SlotSize = 18f;
    private const int Columns = Inventory.HotbarSize;
    private const float Margin = 8f;
    private const float GridWidth = Columns * SlotSize;
    private const float TrackWidth = 10f;
    private const float PanelWidth = Margin + GridWidth + 4f + TrackWidth + Margin;
    private const float PanelHeight = 205f;
    // Where the upper part (the creative items or the crafting list) and its title go, and how
    // many rows of it show; then the main inventory and the hotbar.
    private const float TitleY = 6f;
    private const float UpperY = 17f;
    private const int CreativeRows = 5;
    private const float UpperHeight = CreativeRows * SlotSize;
    private const float RecipeHeight = 22f;
    private const float InventoryTitleY = 111f;
    private const float MainY = 122f;
    private const float HotbarY = 180f;

    // The GNOME palette the inventory's images are drawn in.
    private static readonly GuiColor Face = GuiColor.FromRgb(0x241F31);
    private static readonly GuiColor Edge = GuiColor.FromRgb(0x3D3846);
    private static readonly GuiColor TitleColor = GuiColor.FromRgb(0xDEDDDA);
    private static readonly GuiColor Muted = GuiColor.FromRgb(0x9A9996);
    private static readonly GuiColor Enough = GuiColor.FromRgb(0x8FF0A4);
    private static readonly GuiColor Missing = GuiColor.FromRgb(0xF66151);
    private static readonly GuiColor Highlight = GuiColor.White.WithAlpha(0.3f);
    private static readonly GuiColor Veil = GuiColor.FromRgba(0x101010C8);

    public InventoryScreen(InterfaceArt art, Input? input)
    {
        _art = art;
        _input = input;
    }

    private readonly InterfaceArt _art;
    private readonly Input? _input;
    private float _creativeScroll;
    private float _craftingScroll;
    // What the pointer is over this frame, for its tooltip and for the keys that act on a slot.
    private ItemType _hoveredItem;
    private string? _hoveredNote;
    private int _hoveredSlot;
    private ItemType _hoveredCreative;

    /// <summary>Draws the screen and does what the player does on it.</summary>
    /// <param name="creative">Shows the unlimited catalog beside the inventory.</param>
    /// <param name="workbenchOpened">Whether the player opened a workbench.</param>
    /// <param name="drop">Drops a stack in the world, thrown from the player.</param>
    public void Draw(GuiContext gui, Inventory inventory, bool creative, bool workbenchOpened, Action<ItemStack> drop, Furnace? furnace = null)
    {
        _hoveredItem = ItemType.None;
        _hoveredNote = null;
        _hoveredSlot = -1;
        _hoveredCreative = ItemType.None;
        GuiRect screen = gui.Screen;
        gui.Rect(screen, Veil);
        float width = PanelWidth * (creative ? 2 : 1) + (creative ? 6 : 0);
        float left = MathF.Floor((screen.Width - width) / 2f);
        float top = MathF.Floor((screen.Height - PanelHeight) / 2f);
        GuiRect panel = new(left, top, width, PanelHeight);
        if (creative)
        {
            Raised(gui, new GuiRect(left, top, PanelWidth, PanelHeight));
            gui.Text(new vec2(left + Margin, top + TitleY), "Infinite items", TitleColor, shadow: false);
            GuiRect catalog = new(left + Margin, top + UpperY, GridWidth, PanelHeight - UpperY - Margin);
            CreativeItems(gui, inventory, catalog, new GuiRect(catalog.Right + 4, catalog.Y, TrackWidth, catalog.Height));
            left += PanelWidth + 6;
        }
        Raised(gui, new GuiRect(left, top, PanelWidth, PanelHeight));
        gui.Text(new vec2(left + Margin, top + TitleY), furnace != null ? "Furnace" : workbenchOpened ? "Workbench" : "Crafting", TitleColor, shadow: false);
        GuiRect upper = new(left + Margin, top + UpperY, GridWidth, UpperHeight);
        GuiRect track = new(upper.Right + 4f, upper.Y, TrackWidth, UpperHeight);
        if (furnace != null)
            FurnaceList(gui, inventory, upper, furnace);
        else
            CraftingList(gui, inventory, upper, track, false, workbenchOpened);

        gui.Text(new vec2(left + Margin, top + InventoryTitleY), "Inventory", TitleColor, shadow: false);
        for (int slot = Inventory.HotbarSize; slot < Inventory.Size; slot++)
        {
            int index = slot - Inventory.HotbarSize;
            Slot(gui, inventory, slot, new vec2(left + Margin + index % Columns * SlotSize, top + MainY + index / Columns * SlotSize));
        }

        for (int slot = 0; slot < Inventory.HotbarSize; slot++)
            Slot(gui, inventory, slot, new vec2(left + Margin + slot * SlotSize, top + HotbarY));

        HandleKeys(inventory, drop);
        if (!inventory.Cursor.IsEmpty && !panel.Contains(gui.Cursor))
        {
            if (gui.MousePressed)
            {
                drop(inventory.Cursor);
                inventory.Cursor = ItemStack.Empty;
            }
            else if (gui.SecondaryPressed)
            {
                drop(inventory.Cursor.WithCount(1));
                inventory.Cursor = inventory.Cursor.WithCount(inventory.Cursor.Count - 1);
            }
        }

        if (!inventory.Cursor.IsEmpty)
            Stack(gui, gui.Cursor - 8f, inventory.Cursor);
        else
            Tooltip(gui);
    }

    /// <summary>Puts back in the inventory the stack the pointer carries as the screen closes; what has no room is dropped.</summary>
    public static void Close(Inventory inventory, Action<ItemStack> drop)
    {
        ItemStack left = inventory.Add(inventory.Cursor);
        inventory.Cursor = ItemStack.Empty;
        if (!left.IsEmpty)
            drop(left);
    }

    /// <summary>A panel standing out of the screen: a light top and left edge, a black bottom and right one.</summary>
    private static void Raised(GuiContext gui, GuiRect rect)
    {
        gui.Rect(rect, GuiColor.Black);
        gui.Rect(rect with { Width = rect.Width - 1f, Height = rect.Height - 1f }, Edge);
        gui.Rect(rect.Inset(1f), Face);
    }

    /// <summary>A hollow sunk into the panel, as a slot is: a black top and left edge, a light bottom and right one.</summary>
    private static void Sunk(GuiContext gui, GuiRect rect)
    {
        gui.Rect(rect, Edge);
        gui.Rect(rect with { Width = rect.Width - 1f, Height = rect.Height - 1f }, GuiColor.Black);
        gui.Rect(rect.Inset(1f), Face);
    }

    /// <summary>Every item, a stack of which any click takes: a grid that scrolls.</summary>
    private void CreativeItems(GuiContext gui, Inventory inventory, GuiRect area, GuiRect track)
    {
        ItemType[] items = Items.Creative;
        int rows = (items.Length + Columns - 1) / Columns;
        GuiRect shown = area;
        float scroll = Scroll(gui, "creative", shown, track with { Height = shown.Height }, rows * SlotSize, SlotSize, ref _creativeScroll);

        gui.PushClip(shown);
        for (int i = 0; i < items.Length; i++)
        {
            GuiRect rect = new(area.X + i % Columns * SlotSize, area.Y + i / Columns * SlotSize - scroll, SlotSize, SlotSize);
            gui.Image(rect, _art.Slot);
            Stack(gui, rect.Position + 1f, new ItemStack(items[i], 1));
            if (!gui.IsHovered(rect))
                continue;

            gui.Rect(rect.Inset(1f), Highlight);
            _hoveredItem = items[i];
            _hoveredCreative = items[i];
            ItemStack stack = new(items[i], Items.Get(items[i]).MaxStack);
            if (!gui.MousePressed && !gui.SecondaryPressed)
                continue;

            if (!inventory.Cursor.IsEmpty)
                inventory.Cursor = ItemStack.Empty;
            else if (gui.MousePressed && gui.Input.Shift)
                inventory.Add(stack);
            else
                inventory.Cursor = gui.MousePressed ? stack : stack.WithCount(1);
        }

        gui.PopClip();
    }

    /// <summary>What can be crafted, a row each: what it makes, what it costs, and a click crafts it (SHIFT: as many as can be).</summary>
    private void CraftingList(GuiContext gui, Inventory inventory, GuiRect area, GuiRect track, bool unlimited, bool workbenchOpened)
    {
        Recipe[] recipes = [.. Crafting.Recipes.Where(r => workbenchOpened || !r.NeedsWorkbench)];
        float scroll = Scroll(gui, "crafting", area, track, recipes.Length * RecipeHeight, RecipeHeight, ref _craftingScroll);

        gui.PushClip(area);
        for (int i = 0; i < recipes.Length; i++)
        {
            Recipe recipe = recipes[i];
            GuiRect row = new(area.X, area.Y + i * RecipeHeight - scroll, area.Width, RecipeHeight - 2f);
            if (row.Bottom < area.Y || row.Y > area.Bottom)
                continue;

            bool placeable = !recipe.NeedsWorkbench || workbenchOpened;
            bool craftable = placeable && Crafting.Affords(inventory, recipe, unlimited);
            bool hovered = gui.IsHovered(row);
            Sunk(gui, row);
            if (hovered && craftable)
                gui.Rect(row.Inset(1f), Highlight.WithAlpha(0.12f));

            Stack(gui, new vec2(row.X + 2f, row.Y + 2f), recipe.Result);
            gui.Text(new vec2(row.X + 22f, row.Y + 2f), recipe.Result.Info.Name, craftable ? TitleColor : Muted, shadow: false);
            float x = row.X + 22f;
            for (int k = 0; k < recipe.Cost.Length; k++)
            {
                Ingredient cost = recipe.Cost[k];
                string text = $"{(k > 0 ? ", " : "")}{cost.Count} {cost.Name}";
                bool enough = unlimited || cost.Available(inventory) >= cost.Count;
                x += gui.Text(new vec2(x, row.Y + 12f), text, enough ? Enough : Missing, 6f, shadow: false);
            }

            if (recipe.NeedsWorkbench)
                gui.Image(new GuiRect(row.Right - 11f, row.Y + 2f, 9f, 9f), _art.Icon(BlockType.Workbench), workbenchOpened ? GuiColor.White : GuiColor.White.Scaled(0.35f));

            if (!hovered)
                continue;

            _hoveredItem = recipe.Result.Type;
            _hoveredNote = !placeable ? "Right-click a workbench to craft" : !craftable ? "Not enough to craft it" : null;
            if (craftable && gui.MousePressed)
            {
                int times = gui.Input.Shift ? Items.StackSize : 1;
                for (int k = 0; k < times; k++)
                {
                    if (!Crafting.Craft(inventory, recipe, unlimited))
                        break;
                }
            }
        }

        gui.PopClip();
    }

    private void FurnaceList(GuiContext gui, Inventory inventory, GuiRect area, Furnace furnace)
    {
        for (int i = 0; i < Furnace.Recipes.Length; i++)
        {
            SmeltingRecipe recipe = Furnace.Recipes[i];
            GuiRect row = new(area.X, area.Y + i * 16, area.Width, 15);
            Sunk(gui, row);
            Stack(gui, row.Position, new ItemStack(recipe.Output, 1));
            gui.Text(row.Position + new vec2(20, 3), Items.Get(recipe.Output).Name, TitleColor, shadow: false);
            if (gui.IsHovered(row))
            {
                _hoveredItem = recipe.Output;
                _hoveredNote = "1 " + Items.Get(recipe.Input).Name + " + fuel; 10 seconds";
                if (gui.MousePressed)
                    for (int n = 0; n < (gui.Input.Shift ? 64 : 1); n++)
                        if (!furnace.Start(inventory, recipe)) break;
            }
        }
        GuiRect bar = new(area.X, area.Y + 67, area.Width - 22, 6);
        Sunk(gui, bar);
        gui.Rect(bar.Inset(1) with { Width = (bar.Width - 2) * (float)furnace.Progress }, Enough);
        gui.Text(new vec2(area.X, area.Y + 77), furnace.Queued > 0 ? $"{furnace.Queued} queued: {furnace.Remaining:F1}s" : "Fuel: coal or wood", Muted, 6, shadow: false);
        GuiRect output = new(area.Right - 18, area.Y + 67, 18, 18);
        gui.Image(output, _art.Slot);
        ItemStack result = furnace.Output.Slots[0];
        if (!result.IsEmpty) Stack(gui, output.Position + 1, result);
        if (gui.IsHovered(output))
        {
            _hoveredItem = result.Type;
            if (gui.MousePressed && gui.Input.Shift)
                furnace.Output.Slots[0] = inventory.Add(result);
            else if (gui.MousePressed || gui.SecondaryPressed)
            {
                if (inventory.Cursor.IsEmpty || inventory.Cursor.Type == result.Type)
                {
                    int capacity = result.IsEmpty ? 0 : result.Info.MaxStack - (inventory.Cursor.IsEmpty ? 0 : inventory.Cursor.Count);
                    int count = Math.Min(capacity, gui.SecondaryPressed ? (result.Count + 1) / 2 : result.Count);
                    if (count > 0)
                    {
                        inventory.Cursor = new ItemStack(result.Type, count + (inventory.Cursor.IsEmpty ? 0 : inventory.Cursor.Count));
                        furnace.Output.Slots[0] = result.WithCount(result.Count - count);
                    }
                }
            }
        }
    }

    /// <summary>A slot of the inventory: its stack, and the clicks on it.</summary>
    private void Slot(GuiContext gui, Inventory inventory, int slot, vec2 position)
    {
        GuiRect rect = new(position.x, position.y, SlotSize, SlotSize);
        gui.Image(rect, _art.Slot);
        ItemStack stack = inventory.Slots[slot];
        if (!stack.IsEmpty)
            Stack(gui, position + 1f, stack);
        if (!gui.IsHovered(rect))
            return;

        gui.Rect(rect.Inset(1f), Highlight);
        _hoveredSlot = slot;
        _hoveredItem = stack.IsEmpty ? ItemType.None : stack.Type;
        if (gui.MousePressed)
            inventory.Click(slot, gui.Input.Shift ? SlotClick.Quick : SlotClick.Primary);
        else if (gui.SecondaryPressed)
            inventory.Click(slot, SlotClick.Secondary);
    }

    /// <summary>The number keys and Q over a slot, or a number key over a creative item.</summary>
    private void HandleKeys(Inventory inventory, Action<ItemStack> drop)
    {
        for (int k = 0; k < Inventory.HotbarSize; k++)
        {
            if (_input?.Pressed(Keys.Number1 + k) != true)
                continue;

            if (_hoveredSlot >= 0)
                inventory.SwapWithHotbar(_hoveredSlot, k);
            else if (_hoveredCreative != ItemType.None)
                inventory.Slots[k] = new ItemStack(_hoveredCreative, Items.Get(_hoveredCreative).MaxStack);
        }

        if (_hoveredSlot >= 0 && _input?.Pressed(Keys.Q) == true && !inventory.Slots[_hoveredSlot].IsEmpty)
        {
            bool whole = _input?.Down(Keys.ControlLeft) == true || _input?.Down(Keys.ControlRight) == true;
            drop(inventory.Take(_hoveredSlot, whole ? int.MaxValue : 1));
        }
    }

    /// <summary>
    /// A track beside a list that scrolls, with the knob in it: the wheel over the list or the
    /// track scrolls it a row a notch, and the knob drags. Returns how far (units) it is scrolled.
    /// </summary>
    private float Scroll(GuiContext gui, string id, GuiRect area, GuiRect track, float content, float row, ref float scroll)
    {
        float range = Math.Max(content - area.Height, 0f);
        GuiImage knob = _art.ScrollKnob;
        float travel = track.Height - 2f - knob.Height;
        if (range > 0f && (gui.IsHovered(area) || gui.IsHovered(track)))
            scroll -= gui.Input.Scroll * row;
        if (range > 0f && gui.Drag(id, track))
            scroll = (gui.Cursor.y - track.Y - 1f - knob.Height / 2f) / travel * range;

        scroll = Math.Clamp(scroll, 0f, range);
        Sunk(gui, track);
        float y = track.Y + 1f + (range > 0f ? scroll / range * travel : 0f);
        gui.Image(new GuiRect(track.X + 1f, MathF.Round(y), knob.Width, knob.Height), knob, range > 0f ? GuiColor.White : GuiColor.White.Scaled(0.5f));
        return MathF.Round(scroll);
    }

    /// <summary>An item's icon, 16 units a side from its top-left corner, and how many there are if more than one.</summary>
    private void Stack(GuiContext gui, vec2 position, ItemStack stack)
    {
        gui.Image(new GuiRect(position.x, position.y, 16f, 16f), _art.Icon(stack.Type));
        if (stack.Count <= 1)
            return;

        string count = stack.Count.ToString();
        gui.Text(new vec2(position.x + 17f - gui.MeasureText(count), position.y + 9f), count, GuiColor.White);
    }

    /// <summary>The name of what the pointer is over, in a box beside it, and a note under it if there is one.</summary>
    private void Tooltip(GuiContext gui)
    {
        string? name = _hoveredItem != ItemType.None ? Items.Get(_hoveredItem).Name : null;
        if (name == null && _hoveredNote == null)
            return;

        string[] lines = [.. new[] { name, _hoveredNote }.OfType<string>()];
        float width = lines.Max(line => gui.MeasureText(line)) + 6f;
        float height = lines.Length * 10f + 4f;
        vec2 at = gui.Cursor + new vec2(10f, -14f);
        at.x = Math.Min(at.x, gui.Screen.Width - width);
        at.y = Math.Clamp(at.y, 0f, gui.Screen.Height - height);
        GuiRect box = new(at.x, at.y, width, height);
        gui.Rect(box, GuiColor.Black.WithAlpha(0.9f));
        gui.Outline(box, Edge);
        for (int i = 0; i < lines.Length; i++)
            gui.Text(new vec2(box.X + 3f, box.Y + 3f + 10f * i), lines[i], i == 0 && name != null ? GuiColor.White : Muted);
    }
}
