

namespace MYT.Kit.Vulkan;


public readonly struct VertexAttributeDescriptor
{
    public readonly string Name;
    public readonly int Components;

    public VertexAttributeDescriptor(string name, int components)
    {
        Name = name;
        Components = components;
    }
}


public class VertexAttributes
{
    public readonly VertexAttributeDescriptor[] Layout;
    public readonly double[] Data;
    public readonly int Stride;

    public VertexAttributes(VertexAttributeDescriptor[] layout, double[] data)
    {
        Layout = layout;
        Data = data;

        int stride = 0;
        for (int i = 0; i < layout.Length; i++)
            stride += layout[i].Components;
        Stride = stride;
    }
}
