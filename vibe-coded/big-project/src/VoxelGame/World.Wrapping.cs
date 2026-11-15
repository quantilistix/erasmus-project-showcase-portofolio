using GlmSharp;

namespace VoxelGame;

internal sealed partial class World
{
    /// <summary>Keep one translated representative of each entity near the player.</summary>
    internal void WrapEntities(dvec3 focus)
    {
        static dvec3 Shift(dvec3 position, dvec3 focus) => new(
            -WorldTopology.Width * Math.Floor((position.x - focus.x + WorldTopology.HalfWidth) / WorldTopology.Width),
            -WorldTopology.Width * Math.Floor((position.y - focus.y + WorldTopology.HalfWidth) / WorldTopology.Width), 0);
        foreach (VoxelObject body in _objects)
        {
            dvec3 shift = Shift(body.Position, focus);
            if (shift == dvec3.Zero) continue;
            body.PendingPosition += shift;
            if (body.Body != null) body.Body.Position += shift;
            if (_stepStart.TryGetValue(body, out var start)) _stepStart[body] = start with { Position = start.Position + shift };
        }
        foreach (DroppedItem item in DroppedItems)
        {
            dvec3 shift = Shift(item.Position, focus);
            if (shift == dvec3.Zero) continue;
            item.PendingPosition += shift;
            item.StepStartPosition += shift;
            if (item.Body != null) item.Body.Position += shift;
        }
    }

    internal void WrapFurnaces(ivec2 column)
    {
        if (Furnaces.Count == 0) return;
        ivec2 canonical = WorldTopology.Column(column);
        foreach (var pair in Furnaces.ToArray())
        {
            if (pair.Key.Body != Map) continue;
            ivec2 old = VoxelMap.ColumnOfCell(pair.Key.Cell);
            if (old == column || WorldTopology.Column(old) != canonical) continue;
            Furnaces.Remove(pair.Key);
            Furnaces[(Map, pair.Key.Cell + new ivec3((column - old) * 16, 0))] = pair.Value;
        }
    }
}
