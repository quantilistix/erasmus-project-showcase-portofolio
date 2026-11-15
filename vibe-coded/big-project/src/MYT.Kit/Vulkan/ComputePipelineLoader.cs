

using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Vulkan;

using MYT.Kit;

namespace MYT.Kit.Vulkan;


public readonly struct ComputePipeline
{
    public ComputePipeline(Pipeline pipeline, PipelineLayout layout, DescriptorSetLayout descriptorSetLayout)
    {
        Pipeline = pipeline;
        Layout = layout;
        DescriptorSetLayout = descriptorSetLayout;
    }

    public readonly Pipeline Pipeline;
    public readonly PipelineLayout Layout;
    public readonly DescriptorSetLayout DescriptorSetLayout;
}


public class ComputePipelineLoader : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly List<ShaderModule> _shaderModules = [];
    private readonly List<Pipeline> _pipelines = [];
    private readonly List<PipelineLayout> _pipelineLayouts = [];
    private readonly List<DescriptorSetLayout> _descriptorSetLayouts = [];

    public ComputePipelineLoader(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    public unsafe ComputePipeline Load(string shaderPath, DescriptorSetLayoutBinding[] bindings)
    {
        var source = ReadWithIncludes(shaderPath);

        using var compiler = new ShadercCompiler();
        var spirv = compiler.Compile(source, ShadercShaderKind.ComputeShader, Path.GetFileName(shaderPath));

        var module = CreateShaderModule(spirv);
        _shaderModules.Add(module);

        var descriptorSetLayout = CreateDescriptorSetLayout(bindings);
        _descriptorSetLayouts.Add(descriptorSetLayout);

        var pipelineLayout = CreatePipelineLayout(descriptorSetLayout);
        _pipelineLayouts.Add(pipelineLayout);

        var pipeline = CreateComputePipeline(module, pipelineLayout);
        _pipelines.Add(pipeline);

        return new ComputePipeline(pipeline, pipelineLayout, descriptorSetLayout);
    }

    public unsafe ComputePipeline Load(string shaderPath, DescriptorSetLayout[] descriptorSetLayouts, uint pushConstantSize = 0, bool optimize = false)
    {
        var source = ReadWithIncludes(shaderPath);

        using var compiler = new ShadercCompiler();
        var spirv = compiler.Compile(source, ShadercShaderKind.ComputeShader, Path.GetFileName(shaderPath),optimize:optimize);

        var module = CreateShaderModule(spirv);
        _shaderModules.Add(module);

        var pipelineLayout = CreatePipelineLayout(descriptorSetLayouts, pushConstantSize);
        _pipelineLayouts.Add(pipelineLayout);

        var pipeline = CreateComputePipeline(module, pipelineLayout);
        _pipelines.Add(pipeline);

        return new ComputePipeline(pipeline, pipelineLayout, default);
    }

    /// <summary>Reads a shader source with its includes inlined (see <see cref="ExpandIncludes"/>).</summary>
    internal static string ReadWithIncludes(string shaderPath)
    {
        string fullPath = Path.GetFullPath(shaderPath);
        return ExpandIncludes(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath)!, [fullPath]);
    }

    /// <summary>
    /// Textually inlines <c>#include "file"</c> lines, each file once, relative to the including
    /// file. Shaderc is called without an include callback, so includes are resolved here; the
    /// GL_GOOGLE_include_directive line is dropped because it is only there for offline glslc checks.
    /// </summary>
    private static string ExpandIncludes(string source, string directory, HashSet<string> included)
    {
        StringBuilder result = new();

        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.Trim();

            if (trimmed.Contains("GL_GOOGLE_include_directive"))
                continue;

            if (!trimmed.StartsWith("#include"))
            {
                result.Append(line).Append('\n');
                continue;
            }

            int open = trimmed.IndexOf('"');
            int close = trimmed.LastIndexOf('"');
            if (open < 0 || close <= open)
                throw new InvalidOperationException($"Malformed shader include: {trimmed}");

            string includePath = Path.GetFullPath(Path.Combine(directory, trimmed[(open + 1)..close]));
            if (included.Add(includePath))
                result.Append(ExpandIncludes(File.ReadAllText(includePath), Path.GetDirectoryName(includePath)!, included));
        }

        return result.ToString();
    }

    private unsafe ShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* codePtr = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)codePtr
            };

            if (_vk.CreateShaderModule(_device, &createInfo, null, out ShaderModule module) != Result.Success)
                throw new InvalidOperationException("Failed to create shader module");

            return module;
        }
    }

    private unsafe DescriptorSetLayout CreateDescriptorSetLayout(DescriptorSetLayoutBinding[] bindings)
    {
        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        {
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingsPtr
            };

            if (_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out DescriptorSetLayout layout) != Result.Success)
                throw new InvalidOperationException("Failed to create descriptor set layout");

            return layout;
        }
    }

    private unsafe PipelineLayout CreatePipelineLayout(DescriptorSetLayout descriptorSetLayout)
    {
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayout
        };

        if (_vk.CreatePipelineLayout(_device, &layoutInfo, null, out PipelineLayout pipelineLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create pipeline layout");

        return pipelineLayout;
    }

    private unsafe PipelineLayout CreatePipelineLayout(DescriptorSetLayout[] descriptorSetLayouts, uint pushConstantSize)
    {
        PushConstantRange pushRange = new()
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = pushConstantSize
        };

        fixed (DescriptorSetLayout* layoutsPtr = descriptorSetLayouts)
        {
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)descriptorSetLayouts.Length,
                PSetLayouts = layoutsPtr,
                PushConstantRangeCount = pushConstantSize > 0 ? 1u : 0u,
                PPushConstantRanges = &pushRange
            };

            if (_vk.CreatePipelineLayout(_device, &layoutInfo, null, out PipelineLayout pipelineLayout) != Result.Success)
                throw new InvalidOperationException("Failed to create pipeline layout");

            return pipelineLayout;
        }
    }

    private unsafe Pipeline CreateComputePipeline(ShaderModule module, PipelineLayout layout)
    {
        var entryPoint = Marshal.StringToHGlobalAnsi("main");

        try
        {
            var stageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)entryPoint
            };

            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stageInfo,
                Layout = layout
            };

            if (_vk.CreateComputePipelines(_device, default, 1, &pipelineInfo, null, out Pipeline pipeline) != Result.Success)
                throw new InvalidOperationException("Failed to create compute pipeline");

            return pipeline;
        }
        finally
        {
            Marshal.FreeHGlobal(entryPoint);
        }
    }

    public unsafe void Dispose()
    {
        foreach (var pipeline in _pipelines)
            _vk.DestroyPipeline(_device, pipeline, null);

        foreach (var layout in _pipelineLayouts)
            _vk.DestroyPipelineLayout(_device, layout, null);

        foreach (var descriptorSetLayout in _descriptorSetLayouts)
            _vk.DestroyDescriptorSetLayout(_device, descriptorSetLayout, null);

        foreach (var module in _shaderModules)
            _vk.DestroyShaderModule(_device, module, null);

        _pipelines.Clear();
        _pipelineLayouts.Clear();
        _descriptorSetLayouts.Clear();
        _shaderModules.Clear();

        GC.SuppressFinalize(this);
    }
}
