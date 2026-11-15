using GlmSharp;

namespace VoxelGame;

/// <summary>A complete day and night, starting at noon. All colors are linear RGB.</summary>
internal static class DaylightCycle
{
    // One complete solar day and night.
    public const double DurationSeconds = 32 * 60.0;
    public const double MoonSpeedRatio = 32.0 / 31.0;
    // The sun and moon return to their starting alignment after 31 solar days.
    public const double RepetitionSeconds = DurationSeconds * 31;

    public static Daylight Evaluate(double seconds)
    {
        double phase = seconds / DurationSeconds;
        float angle = (float)((phase - Math.Floor(phase)) * Math.Tau);
        // A tilted orbital plane keeps the sun away from the camera's vertical singularity.
        vec3 noon = new vec3(0.45f, 0.3f, 0.84f).Normalized;
        vec3 east = new vec3(-noon.y, noon.x, 0).Normalized;
        vec3 sun = noon * MathF.Cos(angle) - east * MathF.Sin(angle);
        double moonPhase = phase * MoonSpeedRatio;
        float moonAngle = (float)((moonPhase - Math.Floor(moonPhase)) * Math.Tau);
        vec3 moon = -noon * MathF.Cos(moonAngle) + east * MathF.Sin(moonAngle);
        float day = Smooth(-0.16f, 0.22f, sun.z);
        float twilight = 1 - Smooth(0.06f, 0.65f, Math.Abs(sun.z));
        // Signed altitude gives orange -> yellow at dawn and yellow -> orange at dusk.
        float warmth = 1 - Smooth(-0.2f, 0.28f, sun.z);
        vec3 horizon = Mix(new(0.012f, 0.018f, 0.045f), new(0.55f, 0.72f, 0.95f), day);
        // Twilight starts blue; the shader adds warmth only toward the sun.
        horizon = Mix(horizon, new(0.025f, 0.045f, 0.11f), twilight);
        vec3 zenith = Mix(new(0.0015f, 0.003f, 0.014f), new(0.055f, 0.24f, 0.60f), day);
        zenith = Mix(zenith, new(0.012f, 0.025f, 0.085f), twilight * 0.8f);
        vec3 ambient = Mix(new(0.018f, 0.025f, 0.055f), new(0.55f, 0.72f, 0.95f), day);
        vec3 tint = Mix(new(1f, 0.3f, 0.09f), new(1f, 0.96f, 0.88f), Smooth(0, 0.4f, sun.z));
        return new(sun, moon, horizon, zenith, ambient, tint, Smooth(0.025f, 0.18f, sun.z), twilight, warmth,
            1 - Smooth(-0.22f, 0.04f, sun.z), new vec4(vec3.Cross(noon, east).Normalized, angle));
    }

    private static vec3 Mix(vec3 a, vec3 b, float t) => a + (b - a) * t;
    private static float Smooth(float a, float b, float value)
    {
        float t = Math.Clamp((value - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

internal readonly record struct Daylight(vec3 SunDirection, vec3 MoonDirection, vec3 Horizon, vec3 Zenith, vec3 Ambient,
    vec3 SunColor, float SunIntensity, float Twilight, float Warmth, float Stars, vec4 StarRotation);
