using GlmSharp;

namespace VoxelGame;


/// <summary>
/// How far the held item is from where it rests, in eye space (x right, y forward, z up): moved
/// by <paramref name="Offset"/> (m) and turned about its grip by <paramref name="Turn"/>
/// (radians about each of those axes: x pitches its tip up, y rolls it to the right, z turns
/// it to the left, inward).
/// </summary>
internal readonly record struct HandPose(vec3 Offset, vec3 Turn)
{
    public static HandPose operator +(HandPose a, HandPose b)
    {
        return new HandPose(a.Offset + b.Offset, a.Turn + b.Turn);
    }

    public static HandPose operator -(HandPose a, HandPose b)
    {
        return new HandPose(a.Offset - b.Offset, a.Turn - b.Turn);
    }

    public static HandPose operator *(HandPose pose, float factor)
    {
        return new HandPose(pose.Offset * factor, pose.Turn * factor);
    }

    /// <summary>The pose as a transform of eye space: turned about <paramref name="grip"/>, turn then pitch then roll, then moved.</summary>
    public mat4 About(vec3 grip)
    {
        return mat4.Translate(grip + Offset) * mat4.RotateZ(Turn.z) * mat4.RotateX(Turn.x) * mat4.RotateY(Turn.y) * mat4.Translate(-grip);
    }
}


/// <summary>How the held item swings: a tool chops, its head coming over and down; a block or another item punches, more forward than down; the bare arm jabs, its fist driving forward and in.</summary>
internal enum SwingStyle
{
    Chop,
    Punch,
    Jab
}


/// <summary>
/// The held item's swing, which mining strikes with, in one of the <see cref="SwingStyle"/>s.
/// Every hit starts one: the item winds up, back and up; thrusts forward and in toward the
/// middle of the view, turning as it goes, to the strike, when the hit lands
/// (<see cref="StrikePhase"/> of the way through, 70 ms in); then drops down, pitching over, as
/// it follows through; and settles back to rest, which is most of the swing. A swing takes longer (<see cref="SwingSeconds"/>) than
/// the time between two hits, so while mining the next hit catches it settling, still low, and
/// it swings again from there, blended over <see cref="BlendSeconds"/> so it never jumps. A hit
/// that starts a swing before the last one struck lands that one's hit at once: every hit
/// lands, one per click, six a second held. Each style is three poses (wound up, thrust
/// forward, dropped down), passed through on a smooth curve (cubic Hermite, each keyframe's rate of
/// change set by its neighbours, none at rest), so the item eases and never jerks.
/// </summary>
internal sealed class HeldSwing
{
    /// <summary>How long (s) a whole swing takes, longer than the time between two hits.</summary>
    public const double SwingSeconds = 0.42;

    /// <summary>How far through a swing (0 to 1) it strikes: 70 ms after it starts.</summary>
    public const float StrikePhase = 0.17f;

    /// <summary>How long (s) a swing started during another blends in from where that one left the item.</summary>
    public const double BlendSeconds = 0.06;

    /// <summary>A pose and when in the swing (0 to 1) it is passed through.</summary>
    private readonly record struct Keyframe(float Time, HandPose Pose);

    /// <summary>
    /// A style's poses: wound up; thrust forward, the strike; dropped down, following through.
    /// The swing goes through them from rest and settles back, dipping a little below rest.
    /// </summary>
    private sealed record Style(HandPose WindUp, HandPose Strike, HandPose Down)
    {
        public Keyframe[] Swing { get; } =
        [
            new(0.00f, default),
            new(0.08f, WindUp),
            new(StrikePhase, Strike),
            new(0.36f, Down),
            new(0.65f, Down * 0.25f),
            new(1.00f, default)
        ];
    }

    private static readonly Style Chop = new
    (
        new HandPose(new vec3(0.02f, -0.03f, 0.08f), new vec3(0.60f, 0.14f, -0.14f)),
        new HandPose(new vec3(-0.09f, 0.38f, -0.02f), new vec3(-0.55f, -0.18f, 0.30f)),
        new HandPose(new vec3(-0.10f, 0.22f, -0.21f), new vec3(-1.30f, -0.20f, 0.32f))
    );

    private static readonly Style Punch = new
    (
        new HandPose(new vec3(0.015f, -0.04f, 0.04f), new vec3(0.22f, 0.07f, -0.08f)),
        new HandPose(new vec3(-0.11f, 0.42f, 0.0f), new vec3(-0.25f, -0.15f, 0.38f)),
        new HandPose(new vec3(-0.12f, 0.24f, -0.22f), new vec3(-0.85f, -0.15f, 0.32f))
    );

    // Turned about the shoulder, a deep pitch down would drop the fist out of view: the arm
    // pitches less than the others as it drops.
    private static readonly Style Jab = new
    (
        new HandPose(new vec3(0.015f, -0.05f, 0.0f), new vec3(-0.04f, 0.07f, -0.07f)),
        new HandPose(new vec3(-0.11f, 0.40f, 0.02f), new vec3(0.0f, -0.12f, 0.33f)),
        new HandPose(new vec3(-0.10f, 0.22f, -0.14f), new vec3(-0.30f, -0.08f, 0.25f))
    );

    /// <summary>The pose a fraction <paramref name="phase"/> (0 to 1) of the way through a swing from rest.</summary>
    public static HandPose Sample(float phase, SwingStyle style)
    {
        Keyframe[] keys = style switch
        {
            SwingStyle.Chop => Chop.Swing,
            SwingStyle.Punch => Punch.Swing,
            _ => Jab.Swing
        };

        phase = Math.Clamp(phase, 0f, 1f);
        int k = 0;
        while (k < keys.Length - 2 && phase > keys[k + 1].Time)
            k++;

        Keyframe from = keys[k];
        Keyframe to = keys[k + 1];
        float span = to.Time - from.Time;
        float s = (phase - from.Time) / span;
        float s2 = s * s;
        float s3 = s2 * s;
        return from.Pose * (2f * s3 - 3f * s2 + 1f)
            + Rate(keys, k) * ((s3 - 2f * s2 + s) * span)
            + to.Pose * (3f * s2 - 2f * s3)
            + Rate(keys, k + 1) * ((s3 - s2) * span);
    }

    /// <summary>How fast the pose changes through a keyframe: from the one before it to the one after; none at the ends, which are at rest.</summary>
    private static HandPose Rate(Keyframe[] keys, int k)
    {
        if (k == 0 || k == keys.Length - 1)
            return default;

        return (keys[k + 1].Pose - keys[k - 1].Pose) * (1f / (keys[k + 1].Time - keys[k - 1].Time));
    }

    // How far through the swing playing it is (0 to 1); the pose the one before it left the
    // item in, and how much (1 to 0) of it still shows.
    private double _phase;
    private HandPose _blendFrom;
    private double _blend;

    public bool Playing { get; private set; }

    /// <summary>
    /// Starts a swing from wherever the item is. Returns 1 if the swing it cuts short had not
    /// struck yet, its hit landing now, otherwise 0.
    /// </summary>
    public int Start(SwingStyle style)
    {
        int struck = Playing && _phase < StrikePhase ? 1 : 0;
        _blendFrom = Pose(style);
        _blend = Playing ? 1.0 : 0.0;
        _phase = 0.0;
        Playing = true;
        return struck;
    }

    /// <summary>Moves the swing on by <paramref name="dt"/> (s); returns 1 if it struck meanwhile, otherwise 0.</summary>
    public int Update(double dt)
    {
        _blend = Math.Max(0.0, _blend - dt / BlendSeconds);
        if (!Playing)
            return 0;

        double before = _phase;
        _phase += dt / SwingSeconds;
        int struck = before < StrikePhase && _phase >= StrikePhase ? 1 : 0;
        if (_phase >= 1.0)
        {
            Playing = false;
            _phase = 0.0;
        }

        return struck;
    }

    /// <summary>Where the item is in its swing now; at rest while none plays.</summary>
    public HandPose Pose(SwingStyle style)
    {
        HandPose pose = Playing ? Sample((float)_phase, style) : default;
        float shown = (float)(_blend * _blend * (3.0 - 2.0 * _blend));
        return pose + (_blendFrom - pose) * shown;
    }
}
