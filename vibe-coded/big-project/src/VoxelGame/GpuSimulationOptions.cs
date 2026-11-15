namespace VoxelGame;

/// <summary>Development comparison switches; physics and lighting retain their existing GPU backends.</summary>
internal static class GpuSimulationOptions
{
    internal static bool Enabled(string variable) => Environment.GetEnvironmentVariable("MYT_CPU_REFERENCE") != "1"
        && Environment.GetEnvironmentVariable(variable) != "0";
}
