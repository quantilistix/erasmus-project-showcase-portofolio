namespace NeuralGraph;

/// <summary>Compact streaming checkpoints for graphs with millions of connections.</summary>
internal static class GraphBinary
{
    private const int Magic = 0x4850474E; // NGPH
    public static void Save(GraphModel model, string path)
    {
        model.ValidateValues(); string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!); string temporary = full + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temporary)))
        {
            var t = model.Topology;
            writer.Write(Magic); writer.Write(2); writer.Write(t.InputCount); writer.Write(t.NodeCount); writer.Write(t.Steps);
            writer.Write(t.Outputs.Length); writer.Write(t.Edges.Length); writer.Write(model.IsFrozen);
            foreach (int node in t.Outputs) writer.Write(node);
            foreach (var edge in t.Edges) { writer.Write(edge.Source); writer.Write(edge.Destination); }
            foreach (float parameter in model.Parameters) writer.Write(parameter);
            foreach (float plasticity in model.Plasticity) writer.Write(plasticity);
        }
        File.Move(temporary, full, true);
    }
    public static GraphModel Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadInt32() != Magic || reader.ReadInt32() is not (1 or 2)) throw new InvalidDataException("Unsupported binary checkpoint.");
        int inputs = reader.ReadInt32(), nodes = reader.ReadInt32(), steps = reader.ReadInt32();
        int outputCount = reader.ReadInt32(), edgeCount = reader.ReadInt32(); bool frozen = reader.ReadBoolean();
        if (inputs < 1 || nodes <= inputs || steps < 1 || steps > 256 || outputCount < 1 || outputCount > nodes - inputs || edgeCount < 0 ||
            reader.BaseStream.Length != 29L + 4L * outputCount + 16L * edgeCount + 4L * (nodes - inputs))
            throw new InvalidDataException("Invalid binary checkpoint dimensions or file size.");
        var outputs = new int[outputCount]; for (int i = 0; i < outputs.Length; i++) outputs[i] = reader.ReadInt32();
        var edges = new Edge[edgeCount]; for (int i = 0; i < edges.Length; i++) edges[i] = new(reader.ReadInt32(), reader.ReadInt32());
        var model = new GraphModel(new(inputs, nodes, steps, outputs, edges));
        for (int i = 0; i < model.Parameters.Length; i++) model.Parameters[i] = reader.ReadSingle();
        for (int i = 0; i < model.Plasticity.Length; i++) model.Plasticity[i] = reader.ReadSingle();
        model.ValidateValues(); if (frozen) model.Freeze(); return model;
    }
}
