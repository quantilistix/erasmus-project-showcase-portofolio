

using MYT.IKit;
using MYT.IKit.Vulkan;
using GlmSharp;
using GlmSharp.Swizzle;
using Silk.NET.Maths;
using System;
using System.IO;

namespace MYT.Kit.Vulkan;


public class Mesh2D : IAsset
{
    public static readonly Mesh3DLoader Loader = Mesh3D.Loader;


    public Mesh2D(Vector2D<double>[] vertices, Vector3D<ulong>[] faces)
    {
        Vertices = vertices;
        Faces = faces;
    }

    internal Mesh2D(string filename)
    {

        

    }

    public IMaterial Material;
    public readonly Vector2D<double>[] Vertices;
    public readonly Vector3D<ulong>[] Faces;



}


