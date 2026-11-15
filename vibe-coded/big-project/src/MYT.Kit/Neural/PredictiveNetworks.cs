using NeuralGraph;

namespace MYT.Kit.Neural;

public enum NeuralBackend { Cpu, Vulkan, Auto }

/// <summary>Creates a predictive network for MYT hosts. The caller owns and disposes the backend.
/// Auto falls back only for an unavailable Vulkan runtime or device; model errors still surface.</summary>
public static class PredictiveNetworks
{
    public static IPredictiveCodingBackend Create(PredictiveCodingModel model,
        NeuralBackend backend = NeuralBackend.Auto, int device = -1, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (backend == NeuralBackend.Cpu) return new CpuPredictiveCodingBackend(model);
        if (backend == NeuralBackend.Vulkan) return new VulkanPredictiveCodingBackend(model, device);
        if (backend != NeuralBackend.Auto) throw new ArgumentOutOfRangeException(nameof(backend));
        try { return new VulkanPredictiveCodingBackend(model, device); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or DllNotFoundException)
        {
            diagnostic?.Invoke("Predictive Vulkan unavailable; using CPU: " + e.Message);
            return new CpuPredictiveCodingBackend(model);
        }
    }
}
