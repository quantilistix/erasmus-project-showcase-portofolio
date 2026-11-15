

using System.Runtime.InteropServices;

namespace MYT.Kit;


public enum ShadercShaderKind
{
    VertexShader = 0,
    FragmentShader = 1,
    ComputeShader = 2,
    GeometryShader = 3,
    TessControlShader = 4,
    TessEvaluationShader = 5
}

public sealed class ShadercCompiler : IDisposable
{
    private const string LibName = "shaderc_shared";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint shaderc_compiler_initialize();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void shaderc_compiler_release(nint compiler);

    // shaderc_compile_into_spv takes a trailing shaderc_compile_options_t
    // (nullable) as its 7th argument — omitting it left this a cdecl call
    // short one argument, which read garbage off the stack as that pointer
    // and segfaulted inside the native call on first use.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint shaderc_compile_into_spv(
        nint compiler,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        nuint sourceSize,
        int shaderKind,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputFileName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string entryPoint,
        nint additionalOptions);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int shaderc_result_get_compilation_status(nint result);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint shaderc_result_get_length(nint result);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint shaderc_result_get_bytes(nint result);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint shaderc_result_get_error_message(nint result);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void shaderc_result_release(nint result);


    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint shaderc_compile_options_initialize();
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void shaderc_compile_options_set_optimization_level(nint options, int level);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void shaderc_compile_options_release(nint options);

    private nint compiler;

    public ShadercCompiler()
    {
        compiler = shaderc_compiler_initialize();
        if (compiler == 0)
            throw new InvalidOperationException("Failed to initialize shaderc compiler.");
    }

    public byte[] Compile(string source, ShadercShaderKind kind, string filename, string entryPoint = "main", bool optimize = false)
    {
        ObjectDisposedException.ThrowIf(compiler == 0, this);

        nint options = optimize ? shaderc_compile_options_initialize() : 0;
        if (optimize) shaderc_compile_options_set_optimization_level(options, 2);
        var result = shaderc_compile_into_spv(
            compiler, source, (nuint)System.Text.Encoding.UTF8.GetByteCount(source),
            (int)kind, filename, entryPoint, options);
        if (options != 0) shaderc_compile_options_release(options);

        try
        {
            int status = shaderc_result_get_compilation_status(result);
            if (status != 0) // 0 = success
            {
                var errorPtr = shaderc_result_get_error_message(result);
                var error = Marshal.PtrToStringUTF8(errorPtr) ?? "Unknown compilation error";
                throw new InvalidOperationException($"Shader compilation failed for '{filename}': {error}");
            }

            nuint length = shaderc_result_get_length(result);
            var bytesPtr = shaderc_result_get_bytes(result);

            var spirv = new byte[(int)length];
            Marshal.Copy(bytesPtr, spirv, 0, (int)length);
            return spirv;
        }
        finally
        {
            shaderc_result_release(result);
        }
    }

    public void Dispose()
    {
        if (compiler != 0)
        {
            shaderc_compiler_release(compiler);
            compiler = 0;
        }
    }
}
