using System.Diagnostics;
using System.Runtime.CompilerServices;
using GlmSharp;

namespace VoxelGame;

internal static class LightGrid
{
    public static ivec3 ChunkOf(ivec3 cell) => new(cell.x>>4,cell.y>>4,cell.z>>4);
    public static int IndexInChunk(ivec3 cell) => (cell.x&15)+16*(cell.y&15)+256*(cell.z&15);
    public static readonly ivec3[] FaceDirections = [new(1,0,0),new(-1,0,0),new(0,1,0),new(0,-1,0),new(0,0,1),new(0,0,-1)];
}

internal sealed record LightChunk(ivec3 Coordinate, byte[] Masks, ivec3[] Emitters)
{
    private int[]? _occupied;
    public int[] Occupied => _occupied ??= [.. Enumerable.Range(0,4096).Where(i => Masks[i] != 0)];
}
internal sealed record LightObject(LightChunk[] Chunks, dvec3 Position, dquat Orientation);

/// <summary>An immutable, sparse light volume. Zero means no path from a source, not a dark sky ray.</summary>
internal sealed record LightVolume(uint[] Words, double Milliseconds, int AirCells)
{
    internal static uint Hash(ivec3 p) => unchecked((uint)p.x * 73856093u ^ (uint)p.y * 19349663u ^ (uint)p.z * 83492791u);
    public uint Sample(ivec3 cell)
    {
        if (Words[0] == 0) return 0;
        ivec3 chunk = LightGrid.ChunkOf(cell);
        int mask = (int)Words[0], slot = (int)(Hash(chunk) & (uint)mask);
        for (int probe = 0; probe <= mask; probe++, slot = (slot + 1) & mask)
        {
            int entry = 4 + slot * 4, offset = (int)Words[entry + 3];
            if (offset == 0) return 0;
            if ((int)Words[entry] == chunk.x && (int)Words[entry+1] == chunk.y && (int)Words[entry+2] == chunk.z)
            {
                int index = LightGrid.IndexInChunk(cell), count = (int)Words[offset];
                if (count == 0) return Words[offset+1+index];
                if (count == 1) return Words[offset+1];
                int bits = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8;
                int perWord = 32/bits;
                int palette = (int)((Words[offset+1+count+index/perWord] >> ((index%perWord)*bits)) & ((1u<<bits)-1));
                return Words[offset+1+palette];
            }
        }
        return 0;
    }
    public static float Sky(uint light) => ((light >> 16) & 255) / 255f;
    public static float Block(uint light) => (float)BitConverter.UInt16BitsToHalf((ushort)light);
    public static float Sun(uint light) => (light >> 24) / 255f;
}

/// <summary>Background air-cell propagation with 26-neighbor Euclidean edge costs and no light-radius cutoff.</summary>
internal static class VoxelLightField
{
    private const ushort Unreached = ushort.MaxValue;
    private static readonly byte[] EmptyMasks = new byte[4096];
    private static readonly ushort[] DirectSky = new ushort[4096];
    private readonly record struct Direction(int X, int Y, int Z, int Cost);
    private static readonly Direction[] Directions = MakeDirections();
    private static Direction[] MakeDirections()
    {
        List<Direction> result = [];
        for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            if (x != 0 || y != 0 || z != 0) result.Add(new(x,y,z,(int)Math.Round(16 * Math.Sqrt(x*x+y*y+z*z))));
        return [.. result];
    }
    private sealed class AirChunk(ivec3 coordinate, byte[] masks, bool blockLight, bool exposed)
    {
        public readonly ivec3 Coordinate = coordinate;
        public readonly byte[] Masks = masks;
        public readonly bool Exposed = exposed;
        public readonly ushort[] Sky = exposed ? DirectSky : new ushort[4096];
        public readonly ushort[] Block = blockLight ? new ushort[4096] : [];
        public byte[] Sun = EmptyMasks;
        public readonly AirChunk?[] Neighbors = new AirChunk?[27];
    }
    private readonly record struct Visit(AirChunk Chunk, int Index, ushort Cost);

    // Edge weights are integers in [16,28], so a small circular bucket queue replaces a heap.
    private sealed class Frontier
    {
        private readonly Queue<Visit>[] _buckets = [.. Enumerable.Range(0,29).Select(_ => new Queue<Visit>())];
        private int _count, _distance;
        public void Add(AirChunk chunk, int index, int cost)
        {
            _buckets[cost % 29].Enqueue(new(chunk,index,(ushort)cost));
            _count++;
        }
        public bool Take(out Visit visit)
        {
            if (_count == 0) { visit = default; return false; }
            while (_buckets[_distance % 29].Count == 0) _distance++;
            visit = _buckets[_distance % 29].Dequeue();
            _count--;
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static LightVolume Bake(LightChunk[] terrain, LightObject[] objects, CancellationToken cancel = default)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<ivec3, byte[]> masks = terrain.ToDictionary(c => c.Coordinate, c => c.Masks);
        HashSet<ivec3> writable = [];
        List<ivec3> emitters = [.. terrain.SelectMany(c => c.Emitters)];
        ivec3? terrainLow = terrain.Length == 0 ? null : terrain.Select(c => c.Coordinate).Aggregate(ivec3.Min);
        ivec3? terrainHigh = terrain.Length == 0 ? null : terrain.Select(c => c.Coordinate).Aggregate(ivec3.Max);
        foreach (LightObject body in objects)
        foreach (LightChunk chunk in body.Chunks)
        {
            cancel.ThrowIfCancellationRequested();
            for (int i = 0; i < 4096; i++)
            {
                if (chunk.Masks[i] == 0) continue;
                dvec3 local = new dvec3(chunk.Coordinate * 16 + new ivec3(i & 15, (i >> 4) & 15, i >> 8)) + 0.5;
                ivec3 cell = (ivec3)dvec3.Floor(body.Position + body.Orientation * local);
                ivec3 key = LightGrid.ChunkOf(cell);
                if (terrainLow is ivec3 lo && terrainHigh is ivec3 hi &&
                    (key.x<lo.x || key.x>hi.x || key.y<lo.y || key.y>hi.y || key.z<Math.Min(0,lo.z) || key.z>hi.z+1)) continue;
                if (writable.Add(key)) masks[key] = masks.TryGetValue(key, out byte[]? old) ? (byte[])old.Clone() : new byte[4096];
                // Rasterize rigid blocks on the world-cell grid; transport uses matching open half-cell faces.
                masks[key][LightGrid.IndexInChunk(cell)] |= chunk.Masks[i];
            }
            foreach (ivec3 local in chunk.Emitters)
                emitters.Add((ivec3)dvec3.Floor(body.Position + body.Orientation * (new dvec3(local) + 0.5)));
        }
        if (masks.Count == 0) return new LightVolume(new uint[4], watch.Elapsed.TotalMilliseconds, 0);
        ivec3 minimum = masks.Keys.Aggregate(ivec3.Min), maximum = masks.Keys.Aggregate(ivec3.Max);
        minimum.z = Math.Min(0, minimum.z); maximum.z++;
        // Empty columns between loaded geometry are air too (e.g. a light across a valley).
        List<ivec2> columns = [];
        for (int y = minimum.y; y <= maximum.y; y++)
        for (int x = minimum.x; x <= maximum.x; x++) columns.Add(new(x,y));
        int width = (maximum.x-minimum.x+1)*16, height = (maximum.y-minimum.y+1)*16;
        int[] roof = new int[checked(width*height)];
        Array.Fill(roof, minimum.z*16-1);
        int HeightIndex(int x, int y) => x-minimum.x*16 + width*(y-minimum.y*16);
        foreach (var (key, data) in masks)
        {
            cancel.ThrowIfCancellationRequested();
            bool full = data.AsSpan().IndexOfAnyExcept((byte)255) < 0;
            for (int z = 15; z >= (full ? 15 : 0); z--)
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                if (data[x+16*y+256*z] != 0)
                {
                    int index = HeightIndex(key.x*16+x,key.y*16+y);
                    roof[index] = Math.Max(roof[index],key.z*16+z);
                }
        }
        Dictionary<ivec3,AirChunk> air = [];
        foreach (ivec2 column in columns)
        for (int z = minimum.z; z <= maximum.z; z++)
        {
            cancel.ThrowIfCancellationRequested();
            ivec3 key = new(column,z);
            byte[] data = masks.GetValueOrDefault(key) ?? EmptyMasks;
            if (data.AsSpan().IndexOfAnyExcept((byte)255) < 0) continue;
            int highest = int.MinValue;
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++) highest = Math.Max(highest,roof[HeightIndex(key.x*16+x,key.y*16+y)]);
            bool exposed = key.z*16 > highest;
            AirChunk chunk = new(key,data,emitters.Count > 0,exposed);
            if (chunk.Block.Length > 0) Array.Fill(chunk.Block,Unreached);
            for (int i = 0; !exposed && i < 4096; i++)
            {
                int x = key.x*16+(i&15), y = key.y*16+((i>>4)&15), worldZ = key.z*16+(i>>8);
                chunk.Sky[i] = data[i] == 255 || worldZ <= roof[HeightIndex(x,y)] ? Unreached : (ushort)0;
            }
            air.Add(key,chunk);
        }
        foreach (AirChunk chunk in air.Values)
        for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            chunk.Neighbors[(x+1)+3*(y+1)+9*(z+1)] = air.GetValueOrDefault(chunk.Coordinate+new ivec3(x,y,z));

        Frontier skyFrontier = new();
        foreach (AirChunk chunk in air.Values)
        {
            cancel.ThrowIfCancellationRequested();
            for (int i = 0; !chunk.Exposed && i < 4096; i++)
            {
                if (chunk.Masks[i] == 255 || chunk.Sky[i] != Unreached) continue;
                foreach (ivec3 direction in LightGrid.FaceDirections)
                    if (Neighbor(chunk,i,direction.x,direction.y,direction.z,out AirChunk next,out int index) && next.Sky[index] == 0)
                    { chunk.Sky[i] = 16; skyFrontier.Add(chunk,i,16); break; }
            }
        }
        Propagate(skyFrontier, false,cancel);
        if (emitters.Count > 0)
        {
            Frontier blocks = new();
            foreach (ivec3 source in emitters)
            foreach (ivec3 direction in LightGrid.FaceDirections)
            {
                ivec3 cell = source + direction;
                if (!air.TryGetValue(LightGrid.ChunkOf(cell),out AirChunk? chunk)) continue;
                int i = LightGrid.IndexInChunk(cell);
                int face = direction.x > 0 ? 0x55 : direction.x < 0 ? 0xaa : direction.y > 0 ? 0x33 : direction.y < 0 ? 0xcc : direction.z > 0 ? 0x0f : 0xf0;
                if ((~chunk.Masks[i]&face) == 0 || chunk.Block[i] <= 16) continue;
                chunk.Block[i] = 16; blocks.Add(chunk,i,16);
            }
            Propagate(blocks,true,cancel);
        }
        BakeSun(air,minimum,maximum,cancel);
        int capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(2,air.Count*2));
        List<uint> words = new(new uint[4+capacity*4]);
        words[0] = (uint)(capacity-1);
        Dictionary<uint,int> uniform = [];
        uint[] packed = new uint[4096];
        foreach (AirChunk chunk in air.Values)
        {
            cancel.ThrowIfCancellationRequested();
            bool directUniform = chunk.Exposed && chunk.Block.Length == 0 && chunk.Sun.AsSpan().IndexOfAnyExcept(chunk.Sun[0]) < 0;
            if (directUniform) Array.Fill(packed,255u<<16 | (uint)chunk.Sun[0]<<24);
            for (int i = 0; !directUniform && i < 4096; i++)
            {
                if (chunk.Masks[i] == 255) { packed[i] = 0; continue; }
                double skyDistance = chunk.Sky[i]/16.0;
                uint sky = chunk.Sky[i] == Unreached ? 0 : (uint)Math.Max(1,Math.Round(255/(1+skyDistance*skyDistance/64)));
                double blockDistance = chunk.Block.Length == 0 ? 0 : chunk.Block[i]/16.0;
                float block = chunk.Block.Length == 0 || chunk.Block[i] == Unreached ? 0 : (float)(18/(1+blockDistance*blockDistance));
                packed[i] = HalfBits(block) | sky<<16 | (uint)chunk.Sun[i]<<24;
            }
            bool same = directUniform || packed.All(v => v == packed[0]);
            int offset;
            if (!same || !uniform.TryGetValue(packed[0],out offset))
            {
                offset = words.Count;
                if (same)
                {
                    words.Add(1); words.Add(packed[0]); uniform[packed[0]] = offset;
                }
                else
                {
                    Dictionary<uint,int> palette = [];
                    foreach (uint value in packed)
                        if (!palette.ContainsKey(value)) { palette[value] = palette.Count; if (palette.Count > 256) break; }
                    if (palette.Count > 256) { words.Add(0); words.AddRange(packed); }
                    else
                    {
                        words.Add((uint)palette.Count); words.AddRange(palette.Keys);
                        int bits = palette.Count <= 2 ? 1 : palette.Count <= 4 ? 2 : palette.Count <= 16 ? 4 : 8;
                        int perWord = 32/bits;
                        for (int i = 0; i < 4096; i += perWord)
                        {
                            uint indices = 0;
                            for (int j = 0; j < perWord; j++) indices |= (uint)palette[packed[i+j]] << (j*bits);
                            words.Add(indices);
                        }
                    }
                }
            }
            int slot = (int)(LightVolume.Hash(chunk.Coordinate)&(uint)(capacity-1));
            while (words[4+slot*4+3] != 0) slot = (slot+1)&(capacity-1);
            int entry = 4+slot*4;
            words[entry] = (uint)chunk.Coordinate.x; words[entry+1] = (uint)chunk.Coordinate.y; words[entry+2] = (uint)chunk.Coordinate.z;
            words[entry+3] = (uint)offset;
        }
        return new LightVolume([.. words],watch.Elapsed.TotalMilliseconds,air.Count*4096);
    }

    private static ushort HalfBits(float value)
    {
        if (value <= 0) return 0;
        if (value < 0.00006103515625f) return (ushort)Math.Max(1,Math.Round(value*16777216.0));
        return (ushort)(((BitConverter.SingleToUInt32Bits(value)+0x1000u)>>13)-0x1c000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Neighbor(AirChunk chunk,int index,int dx,int dy,int dz,out AirChunk result,out int target)
    {
        int x = (index&15)+dx, y = ((index>>4)&15)+dy, z = (index>>8)+dz;
        int cx = x<0 ? -1 : x>15 ? 1 : 0, cy = y<0 ? -1 : y>15 ? 1 : 0, cz = z<0 ? -1 : z>15 ? 1 : 0;
        result = chunk.Neighbors[(cx+1)+3*(cy+1)+9*(cz+1)]!;
        target = (x&15)+16*(y&15)+256*(z&15);
        if (result == null || result.Masks[target] == 255) return false;
        int from = ~chunk.Masks[index], to = ~result.Masks[target];
        if (dy == 0 && dz == 0 && dx != 0)
            return dx > 0 ? ((from>>1)&to&0x55) != 0 : (from&(to>>1)&0x55) != 0;
        if (dx == 0 && dz == 0 && dy != 0)
            return dy > 0 ? ((from>>2)&to&0x33) != 0 : (from&(to>>2)&0x33) != 0;
        if (dx == 0 && dy == 0 && dz != 0)
            return dz > 0 ? ((from>>4)&to&0x0f) != 0 : (from&(to>>4)&0x0f) != 0;
        return true;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Propagate(Frontier queue,bool block,CancellationToken cancel)
    {
        int visits = 0;
        while (queue.Take(out Visit visit))
        {
            if ((visits++&8191) == 0) cancel.ThrowIfCancellationRequested();
            ushort[] distances = block ? visit.Chunk.Block : visit.Chunk.Sky;
            if (distances[visit.Index] != visit.Cost) continue;
            foreach (Direction d in Directions)
            {
                if (!Neighbor(visit.Chunk,visit.Index,d.X,d.Y,d.Z,out AirChunk next,out int index)) continue;
                ushort[] values = block ? next.Block : next.Sky;
                int cost = Math.Min(Unreached-1,visit.Cost+d.Cost);
                if (values[index] <= cost) continue;
                // A diagonal must have open axis steps too; light cannot squeeze through solid corners.
                if (d.X != 0 && !Neighbor(visit.Chunk,visit.Index,d.X,0,0,out _,out _)) continue;
                if (d.Y != 0 && !Neighbor(visit.Chunk,visit.Index,0,d.Y,0,out _,out _)) continue;
                if (d.Z != 0 && !Neighbor(visit.Chunk,visit.Index,0,0,d.Z,out _,out _)) continue;
                if (d.X != 0 && d.Y != 0 && d.Z != 0 &&
                    (!Neighbor(visit.Chunk,visit.Index,d.X,d.Y,0,out _,out _) ||
                     !Neighbor(visit.Chunk,visit.Index,d.X,0,d.Z,out _,out _) ||
                     !Neighbor(visit.Chunk,visit.Index,0,d.Y,d.Z,out _,out _))) continue;
                values[index] = (ushort)cost;
                queue.Add(next,index,cost);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BakeSun(Dictionary<ivec3,AirChunk> air,ivec3 low,ivec3 high,CancellationToken cancel)
    {
        int minX = low.x*16, minY = low.y*16, minZ = low.z*16;
        int maxX = (high.x+1)*16, maxY = (high.y+1)*16, maxZ = (high.z+1)*16;
        void Ray(double px,double py,double pz)
        {
            int x = (int)Math.Floor(px), y = (int)Math.Floor(py), z = (int)Math.Floor(pz);
            double tx = (px-x)/0.45, ty = (py-y)/0.3, tz = (pz-z)/0.84;
            ivec3 previous = new(int.MinValue); AirChunk? current = null;
            while (x>=minX && y>=minY && z>=minZ && x<maxX && y<maxY && z<maxZ)
            {
                ivec3 cell = new(x,y,z), key = LightGrid.ChunkOf(cell);
                if (key != previous) { air.TryGetValue(key,out current); previous = key; }
                int index = (x&15)+16*(y&15)+256*(z&15);
                if (current == null || current.Masks[index] == 255) break;
                byte occupied = current.Masks[index];
                bool blocked = false;
                if (occupied != 0)
                {
                    for (int part = 0; part < 8 && !blocked; part++)
                    {
                        if ((occupied & (1<<part)) == 0) continue;
                        double lx = x+(part&1)*0.5, ly = y+((part>>1)&1)*0.5, lz = z+(part>>2)*0.5;
                        double entry = Math.Max(0,Math.Max((px-lx-0.5)/0.45,Math.Max((py-ly-0.5)/0.3,(pz-lz-0.5)/0.84)));
                        double exit = Math.Min((px-lx)/0.45,Math.Min((py-ly)/0.3,(pz-lz)/0.84));
                        blocked = exit > entry;
                    }
                }
                if (blocked) break;
                if (ReferenceEquals(current.Sun,EmptyMasks)) current.Sun = new byte[4096];
                current.Sun[index] = 255;
                if (tx<ty && tx<tz) { x--; tx+=1/0.45; }
                else if (ty<tz) { y--; ty+=1/0.3; }
                else { z--; tz+=1/0.84; }
            }
        }
        for (int y = minY; y < maxY; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = minX; x < maxX; x++) Ray(x+0.5,y+0.5,maxZ-0.0001);
            for (int z = minZ; z < maxZ; z++) Ray(maxX-0.0001,y+0.5,z+0.5);
        }
        for (int x = minX; x < maxX; x++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int z = minZ; z < maxZ; z++) Ray(x+0.5,maxY-0.0001,z+0.5);
        }
    }
}
