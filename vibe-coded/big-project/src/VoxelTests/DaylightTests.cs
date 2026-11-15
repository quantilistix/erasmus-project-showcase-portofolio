using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static class DaylightTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios = [("daylight-cycle", Cycle)];

    private static Daylight At(double days) => DaylightCycle.Evaluate(days * DaylightCycle.DurationSeconds);

    private static bool Cycle()
    {
        bool ok = true;
        void Expect(string name, bool passed)
        {
            Console.WriteLine($"   {(passed ? "ok" : "BAD")} {name}");
            ok &= passed;
        }
        Daylight noon = At(0);
        Daylight night = At(0.5);
        Daylight sunset = At(0.25);
        Daylight sunrise = At(0.75);
        Expect("solar day lasts 32 minutes", DaylightCycle.DurationSeconds == 32 * 60);
        Expect("noon lights the world and hides stars", noon.SunIntensity == 1 && noon.Stars == 0);
        Expect("night removes direct sunlight and reveals stars", night.SunIntensity == 0 && night.Stars == 1
            && night.Ambient.Length < noon.Ambient.Length * 0.1);
        Expect("sunrise and sunset use blue sky beneath directional warmth", sunrise.Twilight > 0.9 && sunset.Twilight > 0.9
            && sunset.Horizon.z > sunset.Horizon.x && sunrise.Horizon.z > sunrise.Horizon.x);
        Expect("sunrise and sunset cross opposite horizons", (sunset.SunDirection + sunrise.SunDirection).Length < 0.00001);
        Expect("dawn changes orange to yellow", At(0.69).Warmth > At(0.81).Warmth);
        Expect("dusk changes yellow to orange", At(0.19).Warmth < At(0.31).Warmth);
        Daylight nextDay = DaylightCycle.Evaluate(DaylightCycle.DurationSeconds);
        double moonAdvance = Math.Acos(Math.Clamp(vec3.Dot(noon.MoonDirection,nextDay.MoonDirection),-1,1));
        Expect("moon gains one thirty-first of an orbit each solar day",
            Math.Abs(moonAdvance-Math.Tau/31) < 0.00001);
        Expect("moon completes an orbit in 31/32 of a solar day",
            (DaylightCycle.Evaluate(DaylightCycle.DurationSeconds/DaylightCycle.MoonSpeedRatio).MoonDirection-noon.MoonDirection).Length < 0.00001);
        Expect("moon remains continuous across the solar day boundary",
            (At(1-0.000001).MoonDirection-At(1+0.000001).MoonDirection).Length < 0.0001);
        Expect("sun and moon realign after 31 solar days",
            (DaylightCycle.Evaluate(DaylightCycle.RepetitionSeconds).MoonDirection-noon.MoonDirection).Length < 0.00001);
        for (int i = 0; i <= 1000; i++)
        {
            double phase = i / 1000.0;
            Daylight current = At(phase);
            Daylight repeated = At(phase + 1);
            ok &= Math.Abs(current.SunDirection.Length - 1) < 0.00001;
            ok &= (current.SunDirection - repeated.SunDirection).Length < 0.00001;
            ok &= float.IsFinite(current.Horizon.Length) && float.IsFinite(current.Zenith.Length);
        }
        Expect("orbit remains finite, normalized and repeats across the whole cycle", ok);
        Expect("wrap is continuous", (At(1 - 0.000001).Horizon
            - At(1e-06).Horizon).Length < 0.0001);
        return ok;
    }

    // Windowed regression: exercise the actual uniform layout, sky pipeline and projection.
    public static bool Sky(GraphicsContext graphics, Renderer renderer, World world, string captures)
    {
        int sequence = 0;
        byte[] Capture(double phase, dvec3 forward, dvec3 eye)
        {
            renderer.Daylight = At(phase);
            if (!graphics.BeginFrame(out Frame frame)) throw new InvalidOperationException("Sky test frame unavailable");
            string path = Path.Combine(captures, $"sky-{sequence++}.ppm");
            graphics.CapturePath = path;
            Camera camera = new(eye,forward,70*Math.PI/180,640.0/480);
            renderer.Draw(frame,camera,world.Bodies,b => (b.Position,b.Orientation),world.Particles,[],null,[],[],false);
            graphics.EndFrame(frame);
            byte[] ppm = File.ReadAllBytes(path);
            int start = 0;
            for (int i = 0; i < 3; i++) start = Array.IndexOf(ppm,(byte)'\n',start)+1;
            return ppm[start..];
        }
        bool ok = true;
        void Expect(string name, bool passed)
        {
            Console.WriteLine($"   {(passed ? "ok" : "BAD")} {name}");
            ok &= passed;
        }
        dvec3 forward = new dvec3(-0.6,-0.4,0.6).Normalized;
        byte[] day = Capture(0,forward,dvec3.Zero);
        byte[] night = Capture(0.5,forward,dvec3.Zero);
        Expect("night sky is darker than day", night.Average(b => (double)b) < day.Average(b => (double)b)*0.5);
        int stars = Enumerable.Range(0,night.Length/3).Count(i => night[i*3] > 120);
        Expect($"night has visible stars ({stars} bright pixels)", stars > 10);
        byte[] laterNight = Capture(0.525,forward,dvec3.Zero);
        int movedStars = Enumerable.Range(0,night.Length/3).Count(i => night[i*3] > 120 && laterNight[i*3] < 80);
        Expect($"stars move as time advances ({movedStars} changed bright pixels)", movedStars > stars/2);
        byte[] repeatedNight = Capture(0.5+DaylightCycle.RepetitionSeconds/DaylightCycle.DurationSeconds,forward,dvec3.Zero);
        Expect("star field and moon return after 31 solar days", night.AsSpan().SequenceEqual(repeatedNight));
        byte[] moved = Capture(0.5,forward,new dvec3(1024,-512,128));
        Expect("sky stays fixed when the camera moves", night.AsSpan().SequenceEqual(moved));
        byte[] repeated = Capture(DaylightCycle.RepetitionSeconds/DaylightCycle.DurationSeconds,forward,dvec3.Zero);
        Expect("rendered sky repeats after 31 solar days", day.AsSpan().SequenceEqual(repeated));
        dvec3 sun = new(At(0).SunDirection);
        byte[] sunImage = Capture(0,sun,dvec3.Zero);
        // Compare clear sky away from the disc/halo at noon, using identical camera
        // coordinates so each sample has the same angular offset from its view direction.
        dvec3 sunHorizon = new dvec3(sun.x,sun.y,0).Normalized;
        byte[] noonHorizon = Capture(0,sunHorizon,dvec3.Zero);
        byte[] noonAway = Capture(0,-sunHorizon,dvec3.Zero);
        int skySample = (240*640+440)*3;
        int Brightness(byte[] pixels,int index) => pixels[index]+pixels[index+1]+pixels[index+2];
        Expect("noon sky near the sun is brighter than the horizon",
            Brightness(sunImage,skySample) > Brightness(noonHorizon,skySample)+20);
        Expect("noon sky darkens away from the sun",
            Brightness(noonHorizon,skySample) > Brightness(noonAway,skySample)+20);
        Expect("noon sky near the sun is bright blue",
            sunImage[skySample+2] > sunImage[skySample] && sunImage[skySample+1] > sunImage[skySample]);
        Expect("daylight cyan has moderate saturation",
            sunImage[skySample] > sunImage[skySample+2]*0.35f
                && sunImage[skySample] < sunImage[skySample+2]*0.6f);
        Expect("the noon sky stays bright away from the sun", noonAway[skySample+2] > 180);
        float nearGreenBlue = sunImage[skySample+1]/(float)sunImage[skySample+2];
        float horizonGreenBlue = noonHorizon[skySample+1]/(float)noonHorizon[skySample+2];
        Expect("daylight hue shifts from cyan near the sun to blue at the horizon",
            nearGreenBlue > horizonGreenBlue+0.15f);
        byte[] moonImage = Capture(0.5,new dvec3(At(0.5).MoonDirection),dvec3.Zero);
        int center = (240*640+320)*3;
        Expect("sun disc is visible", sunImage[center] > 240 && sunImage[center+1] > 240);
        Expect("moon disc is visible", moonImage[center] > 140 && moonImage[center+2] > 140);
        byte[] sunset = Capture(0.25,new dvec3(At(0.25).SunDirection),dvec3.Zero);
        byte[] sunrise = Capture(0.75,new dvec3(At(0.75).SunDirection),dvec3.Zero);
        // A horizon pixel clear of the solar disc should be orange at both crossings.
        int horizon = (240*640+160)*3;
        Expect("sunrise and sunset render warm gradients", sunset[horizon] > sunset[horizon+2]
            && sunrise[horizon] > sunrise[horizon+2]);
        foreach (double phase in new[] { 0.25, 0.75 })
        {
            dvec3 toward = new(At(phase).SunDirection);
            byte[] away = Capture(phase,-toward,dvec3.Zero);
            byte[] towardImage = phase == 0.25 ? sunset : sunrise;
            Expect($"deep blue opposite the sun is stronger at twilight, phase {phase}",
                Brightness(noonAway,horizon) > Brightness(away,horizon)+80);
            Expect($"opposite horizon is darker blue at day phase {phase}", away[horizon+2] > away[horizon]
                && away[horizon]+away[horizon+1]+away[horizon+2]
                    < towardImage[horizon]+towardImage[horizon+1]+towardImage[horizon+2]);
        }
        foreach (double phase in new[] { 0.2, 0.3 })
        {
            vec3 direction = At(phase).SunDirection;
            byte[] glow = Capture(phase,new dvec3(direction.x,direction.y,0).Normalized,dvec3.Zero);
            int above = (120*640+320)*3, below = (360*640+320)*3;
            Expect($"glow follows sun altitude at day phase {phase}", phase < 0.25
                ? glow[above] > glow[below]+15 : glow[below] > glow[above]+15);
            if (phase > 0.25)
                Expect("glow tip stays visible above the horizon after sunset", glow[above] > 50);
        }
        Console.WriteLine($"   Sky captures: {captures}");
        return ok;
    }

}
