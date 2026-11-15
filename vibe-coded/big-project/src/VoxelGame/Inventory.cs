namespace VoxelGame;


/// <summary>How a slot of the inventory screen was clicked.</summary>
internal enum SlotClick
{
    /// <summary>The left button: takes, puts down, merges or swaps the whole stack.</summary>
    Primary,

    /// <summary>The right button: takes half the stack, or puts down one.</summary>
    Secondary,

    /// <summary>SHIFT and the left button: moves the stack between the hotbar and the rest.</summary>
    Quick
}


/// <summary>
/// The player's items, as Minecraft's survival inventory holds them: <see cref="Size"/> slots,
/// the first <see cref="HotbarSize"/> the hotbar, one of which is held, the rest the main
/// inventory; and while the inventory screen is open, the stack the pointer carries. Clicks on
/// slots move stacks as Minecraft's do (<see cref="Click"/>).
/// </summary>
internal sealed class Inventory
{
    public const int HotbarSize = 9;
    public const int Size = 36;

    /// <summary>Hotbar, then the main inventory.</summary>
    public ItemStack[] Slots { get; } = new ItemStack[Size];

    /// <summary>The hotbar slot held.</summary>
    public int Selected
    {
        get;
        set => field = Math.Clamp(value, 0, HotbarSize - 1);
    }

    /// <summary>What the pointer carries on the inventory screen.</summary>
    public ItemStack Cursor { get; set; }

    public ItemStack Held => Slots[Selected];

    public static bool IsHotbar(int slot)
    {
        return slot < HotbarSize;
    }

    /// <summary>
    /// Adds as much of a stack as there is room for: onto the stacks of the same item that are
    /// not full, then into empty slots, in each the hotbar first. Returns what did not fit.
    /// </summary>
    public ItemStack Add(ItemStack stack)
    {
        int left = Add(stack, 0, Size);
        return stack.WithCount(left);
    }

    /// <summary>How many of an item the slots hold.</summary>
    public int Count(ItemType type)
    {
        int count = 0;
        foreach (ItemStack slot in Slots)
        {
            if (!slot.IsEmpty && slot.Type == type)
                count += slot.Count;
        }

        return count;
    }

    /// <summary>Takes <paramref name="count"/> of an item from the slots, the main inventory first. Returns false, taking none, if they hold fewer.</summary>
    public bool Remove(ItemType type, int count)
    {
        if (Count(type) < count)
            return false;

        for (int k = 0; k < Size && count > 0; k++)
        {
            int slot = (k + HotbarSize) % Size;
            if (Slots[slot].IsEmpty || Slots[slot].Type != type)
                continue;

            int taken = Math.Min(count, Slots[slot].Count);
            Slots[slot] = Slots[slot].WithCount(Slots[slot].Count - taken);
            count -= taken;
        }

        return true;
    }

    /// <summary>Takes up to <paramref name="count"/> of what a slot holds and returns it.</summary>
    public ItemStack Take(int slot, int count)
    {
        ItemStack held = Slots[slot];
        int taken = Math.Min(count, held.Count);
        Slots[slot] = held.WithCount(held.Count - taken);
        return held.WithCount(taken);
    }

    /// <summary>A copy, for trying out a change without making it.</summary>
    public Inventory Copy()
    {
        Inventory copy = new() { Selected = Selected, Cursor = Cursor };
        Slots.CopyTo(copy.Slots, 0);
        return copy;
    }

    /// <summary>What a click on a slot does, with the stack the pointer carries.</summary>
    public void Click(int slot, SlotClick click)
    {
        ItemStack held = Slots[slot];
        switch (click)
        {
            case SlotClick.Quick:
                QuickMove(slot);
                break;
            case SlotClick.Primary when Cursor.IsEmpty:
                Cursor = held;
                Slots[slot] = ItemStack.Empty;
                break;
            case SlotClick.Primary when held.IsEmpty || held.Type == Cursor.Type:
                int moved = Math.Min(Cursor.Count, Cursor.Info.MaxStack - held.Count);
                Slots[slot] = Cursor.WithCount(held.Count + moved);
                Cursor = Cursor.WithCount(Cursor.Count - moved);
                break;
            case SlotClick.Secondary when Cursor.IsEmpty:
                int half = (held.Count + 1) / 2;
                Cursor = held.WithCount(half);
                Slots[slot] = held.WithCount(held.Count - half);
                break;
            case SlotClick.Secondary when held.IsEmpty || (held.Type == Cursor.Type && held.Count < held.Info.MaxStack):
                Slots[slot] = Cursor.WithCount(held.Count + 1);
                Cursor = Cursor.WithCount(Cursor.Count - 1);
                break;
            case SlotClick.Secondary when held.Type == Cursor.Type:
                break;
            default:
                Slots[slot] = Cursor;
                Cursor = held;
                break;
        }
    }

    /// <summary>Swaps a slot with a hotbar slot, as a number key over the slot does.</summary>
    public void SwapWithHotbar(int slot, int hotbar)
    {
        (Slots[slot], Slots[hotbar]) = (Slots[hotbar], Slots[slot]);
    }

    /// <summary>Moves a slot's stack from the hotbar into the main inventory or back, as much as fits there.</summary>
    private void QuickMove(int slot)
    {
        ItemStack stack = Slots[slot];
        if (stack.IsEmpty)
            return;

        (int first, int end) = IsHotbar(slot) ? (HotbarSize, Size) : (0, HotbarSize);
        Slots[slot] = stack.WithCount(Add(stack, first, end));
    }

    /// <summary>Adds a stack to the slots in [first, end): onto stacks of the item, then into empty slots. Returns how many did not fit.</summary>
    private int Add(ItemStack stack, int first, int end)
    {
        if (stack.IsEmpty)
            return 0;

        int left = stack.Count;
        int most = stack.Info.MaxStack;
        for (int slot = first; slot < end && left > 0; slot++)
        {
            ItemStack held = Slots[slot];
            if (held.IsEmpty || held.Type != stack.Type || held.Count >= most)
                continue;

            int moved = Math.Min(left, most - held.Count);
            Slots[slot] = held.WithCount(held.Count + moved);
            left -= moved;
        }

        for (int slot = first; slot < end && left > 0; slot++)
        {
            if (!Slots[slot].IsEmpty)
                continue;

            int moved = Math.Min(left, most);
            Slots[slot] = stack.WithCount(moved);
            left -= moved;
        }

        return left;
    }
}
