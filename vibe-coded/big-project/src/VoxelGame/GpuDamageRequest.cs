using System.Runtime.InteropServices;
using GlmSharp;
using MYT.Kit.Structural;

namespace VoxelGame;

/// <summary>External damage/load commands; health and material response are evaluated by the GPU.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuDamageRequest
{
    public dvec4 Input, Stress, Material, Limits;
    internal static GpuDamageRequest Raw(BlockMaterial material, double health, double amount) => new()
    {
        Input = new(health, amount, 0, 0),
        Material = new(material.Durability, material.Stiffness, material.MiningRate, 0),
        Limits = new(material.Elasticity.Tension, material.Elasticity.Compression, material.Elasticity.Shear, material.Unbreakable ? 1 : 0)
    };
    internal static GpuDamageRequest Tool(Voxel voxel, Tool tool, double seconds, CellStress sustained)
    {
        GpuDamageRequest request = Raw(voxel.Material, voxel.Health, 0);
        request.Input.z = tool.Strike; request.Input.w = tool.Speed;
        request.Stress = new(sustained.Tension, sustained.Compression, sustained.Shear, seconds);
        request.Material.w = 1;
        return request;
    }
    internal static GpuDamageRequest Sustained(Voxel voxel, CellStress stress, double seconds, double rate)
    {
        GpuDamageRequest request = Raw(voxel.Material, voxel.Health, rate);
        request.Stress = new(stress.Tension, stress.Compression, stress.Shear, seconds);
        request.Material.w = 2;
        return request;
    }
}
