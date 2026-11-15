

using MYT.IKit;
using MYT.IKit.Vulkan;
using GlmSharp;
using GlmSharp.Swizzle;
using Silk.NET.Maths;
using System;
using System.IO;

namespace MYT.Kit.Vulkan;


public class Mesh4D : IAsset
{
    public static readonly Mesh3DLoader Loader = Mesh3D.Loader;


    public Mesh4D(Vector4D<double>[] vertices, Vector3D<ulong>[] faces)
    {
        Vertices = vertices;
        Faces = faces;
    }

    internal Mesh4D(string filename)
    {

        

    }

    public IMaterial Material;
    public readonly Vector4D<double>[] Vertices;
    public readonly Vector3D<ulong>[] Faces;



}


