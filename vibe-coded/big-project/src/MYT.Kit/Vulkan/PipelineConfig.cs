

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MYT.Kit;


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenderMode
{
    DynamicRendering,
    RenderPass
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkTopology
{
    PointList,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip,
    TriangleFan,
    LineListWithAdjacency,
    LineStripWithAdjacency,
    TriangleListWithAdjacency,
    TriangleStripWithAdjacency,
    PatchList
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkPolygonMode
{
    Fill,
    Line,
    Point
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkCullMode
{
    None,
    Front,
    Back,
    FrontAndBack
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkFrontFace
{
    CounterClockwise,
    Clockwise
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkCompareOp
{
    Never,
    Less,
    Equal,
    LessOrEqual,
    Greater,
    NotEqual,
    GreaterOrEqual,
    Always
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkStencilOp
{
    Keep,
    Zero,
    Replace,
    IncrementAndClamp,
    DecrementAndClamp,
    Invert,
    IncrementAndWrap,
    DecrementAndWrap
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkBlendFactor
{
    Zero,
    One,
    SrcColor,
    OneMinusSrcColor,
    DstColor,
    OneMinusDstColor,
    SrcAlpha,
    OneMinusSrcAlpha,
    DstAlpha,
    OneMinusDstAlpha,
    ConstantColor,
    OneMinusConstantColor,
    ConstantAlpha,
    OneMinusConstantAlpha,
    SrcAlphaSaturate,
    Src1Color,
    OneMinusSrc1Color,
    Src1Alpha,
    OneMinusSrc1Alpha
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkBlendOp
{
    Add,
    Subtract,
    ReverseSubtract,
    Min,
    Max
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkLogicOp
{
    Clear,
    And,
    AndReverse,
    Copy,
    AndInverted,
    NoOp,
    Xor,
    Or,
    Nor,
    Equivalent,
    Invert,
    OrReverse,
    CopyInverted,
    OrInverted,
    Nand,
    Set
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkDynamicState
{
    Viewport,
    Scissor,
    LineWidth,
    DepthBias,
    BlendConstants,
    DepthBounds,
    StencilCompareMask,
    StencilWriteMask,
    StencilReference,
    CullMode,
    FrontFace,
    PrimitiveTopology,
    ViewportWithCount,
    ScissorWithCount,
    DepthTestEnable,
    DepthWriteEnable,
    DepthCompareOp,
    DepthBoundsTestEnable,
    StencilTestEnable,
    StencilOp,
    RasterizerDiscardEnable,
    DepthBiasEnable,
    PrimitiveRestartEnable
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkFormat
{
    Undefined,
    R8Unorm,
    R8Snorm,
    R8G8Unorm,
    R8G8Snorm,
    R8G8B8Unorm,
    R8G8B8Snorm,
    R8G8B8A8Unorm,
    R8G8B8A8Snorm,
    R8G8B8A8Srgb,
    B8G8R8A8Unorm,
    B8G8R8A8Srgb,
    R16Sfloat,
    R16G16Sfloat,
    R16G16B16Sfloat,
    R16G16B16A16Sfloat,
    R32Sfloat,
    R32G32Sfloat,
    R32G32B32Sfloat,
    R32G32B32A32Sfloat,
    R32Sint,
    R32G32Sint,
    R32G32B32Sint,
    R32G32B32A32Sint,
    R32Uint,
    R32G32Uint,
    R32G32B32Uint,
    R32G32B32A32Uint,
    D16Unorm,
    D32Sfloat,
    D24UnormS8Uint,
    D32SfloatS8Uint
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkVertexInputRate
{
    Vertex,
    Instance
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkDescriptorType
{
    Sampler,
    CombinedImageSampler,
    SampledImage,
    StorageImage,
    UniformTexelBuffer,
    StorageTexelBuffer,
    UniformBuffer,
    StorageBuffer,
    UniformBufferDynamic,
    StorageBufferDynamic,
    InputAttachment
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkShaderStage
{
    Vertex,
    TessellationControl,
    TessellationEvaluation,
    Geometry,
    Fragment,
    Compute,
    AllGraphics,
    All
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkSampleCount
{
    Count1,
    Count2,
    Count4,
    Count8,
    Count16,
    Count32,
    Count64
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkAttachmentLoadOp
{
    Load,
    Clear,
    DontCare
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkAttachmentStoreOp
{
    Store,
    DontCare
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkImageLayout
{
    Undefined,
    General,
    ColorAttachmentOptimal,
    DepthStencilAttachmentOptimal,
    DepthStencilReadOnlyOptimal,
    ShaderReadOnlyOptimal,
    TransferSrcOptimal,
    TransferDstOptimal,
    Preinitialized,
    PresentSrcKhr
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkPipelineBindPoint
{
    Graphics,
    Compute
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkPipelineStageFlag
{
    TopOfPipe,
    DrawIndirect,
    VertexInput,
    VertexShader,
    TessellationControlShader,
    TessellationEvaluationShader,
    GeometryShader,
    FragmentShader,
    EarlyFragmentTests,
    LateFragmentTests,
    ColorAttachmentOutput,
    ComputeShader,
    Transfer,
    BottomOfPipe,
    AllGraphics,
    AllCommands
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VkAccessFlag
{
    None,
    IndirectCommandRead,
    IndexRead,
    VertexAttributeRead,
    UniformRead,
    InputAttachmentRead,
    ShaderRead,
    ShaderWrite,
    ColorAttachmentRead,
    ColorAttachmentWrite,
    DepthStencilAttachmentRead,
    DepthStencilAttachmentWrite,
    TransferRead,
    TransferWrite,
    HostRead,
    HostWrite,
    MemoryRead,
    MemoryWrite
}


public class DynamicRenderingConfig
{
    public List<VkFormat> ColorAttachmentFormats { get; set; } = [VkFormat.B8G8R8A8Srgb];
    public VkFormat DepthAttachmentFormat { get; set; } = VkFormat.D32Sfloat;
    public VkFormat StencilAttachmentFormat { get; set; } = VkFormat.Undefined;
}

public class AttachmentConfig
{
    public VkFormat Format { get; set; } = VkFormat.B8G8R8A8Srgb;
    public VkSampleCount Samples { get; set; } = VkSampleCount.Count1;
    public VkAttachmentLoadOp LoadOp { get; set; } = VkAttachmentLoadOp.Clear;
    public VkAttachmentStoreOp StoreOp { get; set; } = VkAttachmentStoreOp.Store;
    public VkAttachmentLoadOp StencilLoadOp { get; set; } = VkAttachmentLoadOp.DontCare;
    public VkAttachmentStoreOp StencilStoreOp { get; set; } = VkAttachmentStoreOp.DontCare;
    public VkImageLayout InitialLayout { get; set; } = VkImageLayout.Undefined;
    public VkImageLayout FinalLayout { get; set; } = VkImageLayout.PresentSrcKhr;
}

public class SubpassConfig
{
    public VkPipelineBindPoint BindPoint { get; set; } = VkPipelineBindPoint.Graphics;
    public List<int> ColorAttachments { get; set; } = [0];
    public int DepthStencilAttachment { get; set; } = -1;
    public List<int> InputAttachments { get; set; } = [];
    public List<int> PreserveAttachments { get; set; } = [];
}

public class SubpassDependencyConfig
{
    public int SrcSubpass { get; set; }
    public int DstSubpass { get; set; }
    public List<VkPipelineStageFlag> SrcStageMask { get; set; } = [];
    public List<VkPipelineStageFlag> DstStageMask { get; set; } = [];
    public List<VkAccessFlag> SrcAccessMask { get; set; } = [];
    public List<VkAccessFlag> DstAccessMask { get; set; } = [];
}

public class RenderPassConfig
{
    public List<AttachmentConfig> Attachments { get; set; } = [];
    public List<SubpassConfig> Subpasses { get; set; } = [];
    public List<SubpassDependencyConfig> Dependencies { get; set; } = [];
}

public class VertexBindingConfig
{
    public uint Binding { get; set; }
    public uint Stride { get; set; }
    public VkVertexInputRate InputRate { get; set; } = VkVertexInputRate.Vertex;
}

public class VertexAttributeConfig
{
    public uint Location { get; set; }
    public uint Binding { get; set; }
    public VkFormat Format { get; set; } = VkFormat.R32G32B32Sfloat;
    public uint Offset { get; set; }
}

public class VertexInputConfig
{
    public List<VertexBindingConfig> Bindings { get; set; } = [];
    public List<VertexAttributeConfig> Attributes { get; set; } = [];
}

public class InputAssemblyConfig
{
    public VkTopology Topology { get; set; } = VkTopology.TriangleList;
    public bool PrimitiveRestartEnable { get; set; } = false;
}

public class RasterizationConfig
{
    public bool DepthClampEnable { get; set; } = false;
    public bool RasterizerDiscardEnable { get; set; } = false;
    public VkPolygonMode PolygonMode { get; set; } = VkPolygonMode.Fill;
    public VkCullMode CullMode { get; set; } = VkCullMode.Back;
    public VkFrontFace FrontFace { get; set; } = VkFrontFace.CounterClockwise;
    public bool DepthBiasEnable { get; set; } = false;
    public float DepthBiasConstantFactor { get; set; } = 0.0f;
    public float DepthBiasClamp { get; set; } = 0.0f;
    public float DepthBiasSlopeFactor { get; set; } = 0.0f;
    public float LineWidth { get; set; } = 1.0f;
}

public class MultisamplingConfig
{
    public VkSampleCount RasterizationSamples { get; set; } = VkSampleCount.Count1;
    public bool SampleShadingEnable { get; set; } = false;
    public float MinSampleShading { get; set; } = 1.0f;
    public bool AlphaToCoverageEnable { get; set; } = false;
    public bool AlphaToOneEnable { get; set; } = false;
}

public class StencilOpConfig
{
    public VkStencilOp FailOp { get; set; } = VkStencilOp.Keep;
    public VkStencilOp PassOp { get; set; } = VkStencilOp.Keep;
    public VkStencilOp DepthFailOp { get; set; } = VkStencilOp.Keep;
    public VkCompareOp CompareOp { get; set; } = VkCompareOp.Always;
    public uint CompareMask { get; set; } = 0xFF;
    public uint WriteMask { get; set; } = 0xFF;
    public uint Reference { get; set; } = 0;
}

public class DepthStencilConfig
{
    public bool DepthTestEnable { get; set; } = true;
    public bool DepthWriteEnable { get; set; } = true;
    public VkCompareOp DepthCompareOp { get; set; } = VkCompareOp.Less;
    public bool DepthBoundsTestEnable { get; set; } = false;
    public float MinDepthBounds { get; set; } = 0.0f;
    public float MaxDepthBounds { get; set; } = 1.0f;
    public bool StencilTestEnable { get; set; } = false;
    public StencilOpConfig Front { get; set; } = new();
    public StencilOpConfig Back { get; set; } = new();
}

public class ColorBlendAttachmentConfig
{
    public bool BlendEnable { get; set; } = false;
    public VkBlendFactor SrcColorBlendFactor { get; set; } = VkBlendFactor.One;
    public VkBlendFactor DstColorBlendFactor { get; set; } = VkBlendFactor.Zero;
    public VkBlendOp ColorBlendOp { get; set; } = VkBlendOp.Add;
    public VkBlendFactor SrcAlphaBlendFactor { get; set; } = VkBlendFactor.One;
    public VkBlendFactor DstAlphaBlendFactor { get; set; } = VkBlendFactor.Zero;
    public VkBlendOp AlphaBlendOp { get; set; } = VkBlendOp.Add;
    public bool ColorWriteR { get; set; } = true;
    public bool ColorWriteG { get; set; } = true;
    public bool ColorWriteB { get; set; } = true;
    public bool ColorWriteA { get; set; } = true;
}

public class ColorBlendConfig
{
    public bool LogicOpEnable { get; set; } = false;
    public VkLogicOp LogicOp { get; set; } = VkLogicOp.Copy;
    public List<ColorBlendAttachmentConfig> Attachments { get; set; } = [new()];
    public float[] BlendConstants { get; set; } = [0.0f, 0.0f, 0.0f, 0.0f];
}

public class PushConstantRangeConfig
{
    public List<VkShaderStage> StageFlags { get; set; } = [VkShaderStage.Vertex];
    public uint Offset { get; set; } = 0;
    public uint Size { get; set; }
}

public class DescriptorBindingConfig
{
    public uint Binding { get; set; }
    public VkDescriptorType DescriptorType { get; set; } = VkDescriptorType.UniformBuffer;
    public uint DescriptorCount { get; set; } = 1;
    public List<VkShaderStage> StageFlags { get; set; } = [VkShaderStage.Vertex];
}

public class DescriptorSetLayoutConfig
{
    public List<DescriptorBindingConfig> Bindings { get; set; } = [];
}

public class ViewportConfig
{
    public float X { get; set; } = 0.0f;
    public float Y { get; set; } = 0.0f;
    public float Width { get; set; } = 800.0f;
    public float Height { get; set; } = 600.0f;
    public float MinDepth { get; set; } = 0.0f;
    public float MaxDepth { get; set; } = 1.0f;
    public int ScissorX { get; set; } = 0;
    public int ScissorY { get; set; } = 0;
    public uint ScissorWidth { get; set; } = 800;
    public uint ScissorHeight { get; set; } = 600;
}


public class PipelineConfig
{
    public RenderMode RenderMode { get; set; } = RenderMode.DynamicRendering;
    public DynamicRenderingConfig DynamicRendering { get; set; } = new();
    public RenderPassConfig RenderPass { get; set; } = new();
    public VertexInputConfig VertexInput { get; set; } = new();
    public InputAssemblyConfig InputAssembly { get; set; } = new();
    public RasterizationConfig Rasterization { get; set; } = new();
    public MultisamplingConfig Multisampling { get; set; } = new();
    public DepthStencilConfig DepthStencil { get; set; } = new();
    public ColorBlendConfig ColorBlend { get; set; } = new();
    public ViewportConfig Viewport { get; set; } = new();
    public List<VkDynamicState> DynamicStates { get; set; } = [VkDynamicState.Viewport, VkDynamicState.Scissor];
    public List<PushConstantRangeConfig> PushConstantRanges { get; set; } = [];
    public List<DescriptorSetLayoutConfig> DescriptorSetLayouts { get; set; } = [];


    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static PipelineConfig LoadFromJson(string json)
    {
        return JsonSerializer.Deserialize<PipelineConfig>(json, SerializerOptions)
               ?? throw new InvalidDataException("Failed to deserialize pipeline config.");
    }

    public string SaveToJson()
    {
        return JsonSerializer.Serialize(this, SerializerOptions);
    }
}
