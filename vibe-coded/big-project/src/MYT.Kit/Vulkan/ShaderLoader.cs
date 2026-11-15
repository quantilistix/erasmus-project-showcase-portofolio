

using Silk.NET.Vulkan;


namespace MYT.Kit.Vulkan;


public class ShaderLoader : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly List<ShaderModule> _modules = [];

    public ShaderLoader(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    public ShaderModule LoadVertex(string glslPath)
    {
        return LoadShader(glslPath, ShadercShaderKind.VertexShader);
    }

    public ShaderModule LoadFragment(string glslPath)
    {
        return LoadShader(glslPath, ShadercShaderKind.FragmentShader);
    }

    public ShaderModule LoadGeometry(string glslPath)
    {
        return LoadShader(glslPath, ShadercShaderKind.GeometryShader);
    }

    public ShaderModule LoadCompute(string glslPath)
    {
        return LoadShader(glslPath, ShadercShaderKind.ComputeShader);
    }

    private unsafe ShaderModule LoadShader(string glslPath, ShadercShaderKind kind)
    {
        string source = ComputePipelineLoader.ReadWithIncludes(glslPath);

        using ShadercCompiler compiler = new();
        byte[] spirv = compiler.Compile(source, kind, Path.GetFileName(glslPath));

        fixed (byte* codePtr = spirv)
        {
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)codePtr
            };

            if (_vk.CreateShaderModule(_device, &createInfo, null, out ShaderModule module) != Result.Success)
                throw new InvalidOperationException($"Failed to create shader module from '{glslPath}'");

            _modules.Add(module);
            return module;
        }
    }

    public unsafe void Dispose()
    {
        foreach (ShaderModule module in _modules)
            _vk.DestroyShaderModule(_device, module, null);

        _modules.Clear();

        GC.SuppressFinalize(this);
    }
}
