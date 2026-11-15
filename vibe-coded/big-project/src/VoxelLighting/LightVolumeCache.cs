using System.Runtime.InteropServices;
using GlmSharp;

namespace VoxelGame;

/// <summary>RGB packing and per-frame upload tracking, optimized in both game configurations.</summary>
internal sealed class LightVolumeCache
{
    private readonly Dictionary<ivec3,(int Offset,int Revision,int Capacity)> _packed = [];
    private readonly Dictionary<int,HashSet<ivec3>> _uploaded = [];
    private readonly Dictionary<int,int> _uploadedTopology = [];
    private readonly Dictionary<ivec3,(int Offset,int Revision)> _points = [];
    private readonly Dictionary<int,Dictionary<ivec3,int>> _uploadedPoints = [];
    private readonly Stack<int> _freePoints = [];
    private int _pointList, _pointCapacity;
    private const int PointWords = 4+6*RealtimeLightField.PointShadow.Size*RealtimeLightField.PointShadow.Size;
    private readonly Dictionary<int,Stack<int>> _freeChunks = [];
    private readonly ulong[] _cellWords = new ulong[4096];
    private readonly Dictionary<ulong,int> _palette = [];
    private Stack<int> FreeChunks(int capacity)
    {
        if (!_freeChunks.TryGetValue(capacity,out var free)) _freeChunks[capacity] = free = [];
        return free;
    }
    private readonly Dictionary<ivec2,int> _roofs = [];
    private readonly Stack<int> _freeRoofs = [];
    private readonly Dictionary<int,HashSet<ivec2>> _roofUploads = [];
    private int _roofVersion = -1, _roofHash, _roofCapacity;
    private static uint RoofHash(ivec2 p) => unchecked((uint)p.x*73856093u ^ (uint)p.y*19349663u);
    private uint[] _words = new uint[8];
    private int _topology = -1, _header = 8, _used = 8;
    public int Version { get; private set; }
    public bool Ready => _words[0] != 0;
    public int Bytes => _used*sizeof(uint);
    private static ushort HalfBits(float value)
    {
        if (value <= 0) return 0;
        if (value < .00006103515625f) return (ushort)Math.Max(1,Math.Round(value*16777216.0));
        return (ushort)(((BitConverter.SingleToUInt32Bits(Math.Min(value,65504)) + 0x1000u)>>13)-0x1c000u);
    }
    private void Reserve(int length)
    {
        if (_words.Length >= length) return;
        Array.Resize(ref _words,(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)length));
    }
    public void Update(RealtimeLightField field)
    {
        bool topology = _topology != field.TopologyVersion;
        if (topology)
        {
            RealtimeLightField.Chunk[] chunks = [.. field.Chunks];
            int capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(256,chunks.Length*2));
            int header = 8+capacity*4;
            if (header != _header)
            {
                _header = header; _used = header; _packed.Clear(); _freeChunks.Clear();
                _uploaded.Clear(); _uploadedTopology.Clear();
                _points.Clear(); _uploadedPoints.Clear(); _freePoints.Clear(); _pointList = _pointCapacity = 0;
                _roofs.Clear(); _freeRoofs.Clear(); _roofUploads.Clear(); _roofVersion = -1; _roofHash = _roofCapacity = 0;
            }
            Reserve(_header);
            Array.Clear(_words,0,_header); _words[0] = (uint)(capacity-1);
            HashSet<ivec3> live = [.. chunks.Select(c => c.Key)];
            foreach (ivec3 key in _packed.Keys.Where(k => !live.Contains(k)).ToArray())
            {
                var freed = _packed[key];
                FreeChunks(freed.Capacity).Push(freed.Offset);
                _packed.Remove(key);
            }
            _topology = field.TopologyVersion;
        }
        bool changed = topology;
        foreach (var c in topology ? field.Chunks : field.DirtyChunks)
        {
            var existing = _packed.GetValueOrDefault(c.Key,(0,-1,0));
            var (offset,revision,capacity) = existing;
            if (revision != c.Revision)
            {
                _palette.Clear();
                for (int i = 0; i < 4096; i++)
                {
                    vec3 rgb = c.Light(i);
                    uint rg = HalfBits(rgb.x) | (uint)HalfBits(rgb.y)<<16;
                    uint bs = HalfBits(rgb.z) | (field.GpuSunShadows ? 0u : (uint)c.Sun(i)<<16) | (uint)c.Sky(i)<<24;
                    ulong value = rg | (ulong)bs<<32; _cellWords[i] = value;
                    if (_palette.Count <= 256 && !_palette.ContainsKey(value)) _palette[value] = _palette.Count;
                }
                int count = _palette.Count > 256 ? 0 : _palette.Count;
                int bits = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8;
                int perWord = 32/bits;
                int needed = count == 0 ? 8193 : count == 1 ? 3 : 1+2*count+4096/perWord;
                int allocation = count == 0 ? 8193 : count == 1 ? 3 : count <= 2 ? 133 : count <= 4 ? 265 : count <= 16 ? 545 : 1537;
                if (capacity < needed)
                {
                    if (capacity != 0) FreeChunks(capacity).Push(offset);
                    Stack<int> free = FreeChunks(allocation);
                    capacity = allocation;
                    if (free.Count > 0) offset = free.Pop();
                    else { offset = _used; _used += allocation; Reserve(_used); }
                }
                _words[offset] = (uint)count;
                if (count == 0)
                {
                    for (int i = 0; i < 4096; i++)
                    { _words[offset+1+i*2] = (uint)_cellWords[i]; _words[offset+2+i*2] = (uint)(_cellWords[i]>>32); }
                }
                else
                {
                    foreach (var (value,index) in _palette)
                    { _words[offset+1+index*2] = (uint)value; _words[offset+2+index*2] = (uint)(value>>32); }
                    if (count > 1)
                        for (int i = 0; i < 4096; i += perWord)
                        {
                            uint indices = 0;
                            for (int j = 0; j < perWord; j++) indices |= (uint)_palette[_cellWords[i+j]] << (j*bits);
                            _words[offset+1+count*2+i/perWord] = indices;
                        }
                }
                _packed[c.Key] = (offset,c.Revision,capacity);
                foreach (var pending in _uploaded.Values) pending.Add(c.Key);
                changed = true;
            }
            if (topology || offset != existing.Item1)
            {
                uint mask = _words[0];
                int slot = (int)(LightVolume.Hash(c.Key)&mask);
                while (_words[8+slot*4+3] != 0 &&
                    ((int)_words[8+slot*4] != c.Key.x || (int)_words[9+slot*4] != c.Key.y || (int)_words[10+slot*4] != c.Key.z))
                    slot = (slot+1)&(int)mask;
                int entry = 8+slot*4;
                _words[entry] = (uint)c.Key.x; _words[entry+1] = (uint)c.Key.y; _words[entry+2] = (uint)c.Key.z; _words[entry+3] = (uint)offset;
                // An existing uniform chunk may need a larger allocation as a light wave arrives.
                if (!topology) { _uploadedTopology.Clear(); }
            }
        }
        RealtimeLightField.PointShadow[] points = [.. field.PointLights];
        HashSet<ivec3> livePoints = [.. points.Select(p => p.Cell)];
        bool pointTable = _words[1] != (uint)points.Length;
        foreach (ivec3 key in _points.Keys.Where(k => !livePoints.Contains(k)).ToArray())
        { _freePoints.Push(_points[key].Offset); _points.Remove(key); pointTable = true; }
        if (_pointCapacity < points.Length)
        {
            _pointCapacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)points.Length);
            _pointList = _used; _used += _pointCapacity; Reserve(_used); pointTable = true;
        }
        for (int i = 0; i < points.Length; i++)
        {
            var point = points[i];
            var (offset,revision) = _points.GetValueOrDefault(point.Cell,(0,-1));
            if (offset == 0)
            {
                if (_freePoints.Count > 0) offset = _freePoints.Pop();
                else { offset = _used; _used += PointWords; Reserve(_used); }
                pointTable = true;
            }
            if (revision != point.Revision)
            {
                _words[offset] = BitConverter.SingleToUInt32Bits(point.Cell.x+.5f);
                _words[offset+1] = BitConverter.SingleToUInt32Bits(point.Cell.y+.5f);
                _words[offset+2] = BitConverter.SingleToUInt32Bits(point.Cell.z+.5f);
                _words[offset+3] = 0;
                for (int j = 0; j < point.Depth.Length; j++) _words[offset+4+j] = BitConverter.SingleToUInt32Bits(point.Depth[j]);
                _points[point.Cell] = (offset,point.Revision); changed = true;
            }
            _words[_pointList+i] = (uint)offset;
        }
        _words[1] = (uint)points.Length; _words[2] = (uint)_pointList; _words[3] = RealtimeLightField.PointShadow.Size;
        if (pointTable) { _uploadedTopology.Clear(); changed = true; }
        changed |= UpdateRoofs(field);
        field.ClearDirty();
        if (changed) Version++;
    }
    public void InvalidateUpload(int frame)
    {
        _uploaded.Remove(frame); _uploadedTopology.Remove(frame); _uploadedPoints.Remove(frame); _roofUploads.Remove(frame);
    }
    public void Write(Span<byte> destination,int frame = 0)
    {
        Span<uint> target = MemoryMarshal.Cast<byte,uint>(destination);
        if (!_uploaded.TryGetValue(frame,out var pending)) _uploaded[frame] = pending = [.. _packed.Keys];
        if (_uploadedTopology.GetValueOrDefault(frame,-1) != _topology)
        {
            _words.AsSpan(0,_header).CopyTo(target);
            if (_words[1] != 0) _words.AsSpan(_pointList,(int)_words[1]).CopyTo(target[_pointList..]);
            if (_roofCapacity != 0) _words.AsSpan(_roofHash,_roofCapacity*4).CopyTo(target[_roofHash..]);
            _uploadedTopology[frame] = _topology;
        }
        if (!_uploadedPoints.TryGetValue(frame,out var points)) _uploadedPoints[frame] = points = [];
        foreach (var (key,(offset,revision)) in _points)
        {
            if (points.GetValueOrDefault(key,-1) == revision) continue;
            _words.AsSpan(offset,PointWords).CopyTo(target[offset..]); points[key] = revision;
        }
        foreach (ivec3 key in pending)
        {
            if (!_packed.TryGetValue(key,out var entry)) continue;
            int offset = entry.Offset;
            int count = (int)_words[offset];
            int bits = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8;
            int length = count == 0 ? 8193 : count == 1 ? 3 : 1+count*2+4096/(32/bits);
            _words.AsSpan(offset,length).CopyTo(target[offset..]);
        }
        pending.Clear();
        if (!_roofUploads.TryGetValue(frame,out var roofs)) _roofUploads[frame] = roofs = [.. _roofs.Keys];
        foreach (ivec2 key in roofs)
            if (_roofs.TryGetValue(key,out int offset)) _words.AsSpan(offset,256).CopyTo(target[offset..]);
        roofs.Clear();
    }
    private bool UpdateRoofs(RealtimeLightField field)
    {
        bool topology = _roofVersion != field.RoofTopologyVersion;
        bool reset = _roofVersion < 0;
        if (topology)
        {
            foreach (ivec2 key in _roofs.Keys.Where(k => !field.RoofColumns.ContainsKey(k)).ToArray())
            { _freeRoofs.Push(_roofs[key]); _roofs.Remove(key); }
            int needed = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(2,field.RoofColumns.Count*2));
            if (_roofCapacity < needed)
            { _roofCapacity = needed; _roofHash = _used; _used += needed*4; Reserve(_used); }
            Array.Clear(_words,_roofHash,_roofCapacity*4);
            foreach (ivec2 key in field.RoofColumns.Keys)
            {
                if (!_roofs.TryGetValue(key,out int offset))
                {
                    if (_freeRoofs.Count > 0) offset = _freeRoofs.Pop();
                    else { offset = _used; _used += 256; Reserve(_used); }
                    _roofs[key] = offset;
                }
                int slot = (int)(RoofHash(key)&(uint)(_roofCapacity-1));
                while (_words[_roofHash+slot*4+2] != 0) slot = (slot+1)&(_roofCapacity-1);
                int entry = _roofHash+slot*4;
                _words[entry] = (uint)key.x; _words[entry+1] = (uint)key.y; _words[entry+2] = (uint)offset;
            }
            _roofVersion = field.RoofTopologyVersion; _uploadedTopology.Clear();
        }
        bool changed = topology;
        foreach (ivec2 key in reset ? field.RoofColumns.Keys : field.DirtyRoofs)
        {
            if (!_roofs.TryGetValue(key,out int offset)) continue;
            MemoryMarshal.Cast<int,uint>(field.RoofColumns[key].AsSpan()).CopyTo(_words.AsSpan(offset,256));
            foreach (var pending in _roofUploads.Values) pending.Add(key);
            changed = true;
        }
        _words[4] = (uint)(_roofCapacity-1); _words[5] = (uint)_roofHash;
        field.ClearDirtyRoofs();
        return changed;
    }
    public void Clear()
    {
        _packed.Clear(); _freeChunks.Clear(); _uploaded.Clear(); _uploadedTopology.Clear();
        _points.Clear(); _uploadedPoints.Clear(); _freePoints.Clear(); _pointList = _pointCapacity = 0;
                _roofs.Clear(); _freeRoofs.Clear(); _roofUploads.Clear(); _roofVersion = -1; _roofHash = _roofCapacity = 0;
        _words = new uint[8]; _topology = -1; _header = 8; _used = 8; Version++;
    }
}
