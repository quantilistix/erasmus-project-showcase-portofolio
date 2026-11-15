using GlmSharp;

namespace VoxelGame;

/// <summary>Voxel/material inputs for the GPU mesher. No generated geometry is kept here.</summary>
internal static class GpuMeshInputs
{
    internal const int Variants = 512; // every orientation byte, unlit/lit
    internal const int WordsPerVariant = 8;
    internal const int InputWords = VoxelMesher.PaddedVolume + 4096;
    internal static uint Tag(BlockType type) => (uint)type * Variants * WordsPerVariant;
    internal static uint[] Pack(Voxel[] cells)
    {
        uint[] packed = new uint[cells.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            Voxel voxel = cells[i];
            packed[i] = (uint)(((int)voxel.Type * Variants + (byte)voxel.Orientation * 2 + (voxel.Lit ? 1 : 0)) * WordsPerVariant);
        }
        return packed;
    }

    internal static uint[] Materials(int[,] layers)
    {
        uint[] materials = new uint[Blocks.Count * Variants * WordsPerVariant];
        for (int type = 0; type < Blocks.Count; type++)
        for (int orientation = 0; orientation < 256; orientation++)
        for (int lit = 0; lit < 2; lit++)
        {
            Voxel voxel = new((BlockType)type, 0, (BlockOrientation)orientation, lit != 0);
            int first = (type * Variants + orientation * 2 + lit) * WordsPerVariant;
            materials[first] = (voxel.Material.Transparent ? 1u : 0)
                | (voxel.Type == BlockType.Air ? 2u : 0) | (voxel.Type == BlockType.Water ? 4u : 0)
                | (voxel.Type == BlockType.Leaves ? 8u : 0) | (BlockShapes.IsDoor(voxel.Type) ? 16u : 0)
                | (uint)(orientation & 3) << 5;
            for (int face = 0; face < 6; face++)
                materials[first + face + 1] = (uint)voxel.TextureLayer(layers, face) | ((uint)voxel.TextureRotation(face) & 3) << 8;
            for (int part = 0; part < 8; part++)
                if (BlockShapes.Occupied(voxel, new ivec3(part & 1, (part >> 1) & 1, part >> 2))) materials[first + 7] |= 1u << part;
        }
        return materials;
    }
}
