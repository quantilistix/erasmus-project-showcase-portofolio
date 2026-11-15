namespace ArcadeLab;

public enum GameKind { Catch, Dodge, Rally }
[Flags] public enum Buttons { None = 0, Left = 1, Right = 2 }

/// <summary>The agent sees only rasterized light and synthesized PCM, never these private physics fields.</summary>
public sealed class ArcadeGame
{
    public const int Width = 24, Height = 18, AudioSize = 64, SampleRate = 1280;
    private readonly Random random;
    public GameKind Kind { get; }
    public float[] Pixels { get; } = new float[Width * Height];
    public float[] Audio { get; } = new float[AudioSize];
    public int Tick { get; private set; }
    public int Score { get; private set; }
    public bool CaughtThisFrame { get; private set; }
    public RewardTeachingTarget RewardTarget { get; private set; }
    public float GameSpeed { get; private set; }
    public int EpisodeFrames => (int)Math.Ceiling(96 / GameSpeed);
    public bool Done => Tick >= EpisodeFrames;
    public void SetSpeed(float speed)
    {
        if (!float.IsFinite(speed) || speed < .05f || speed > 2) throw new ArgumentOutOfRangeException(nameof(speed));
        GameSpeed = speed;
    }
    private float player, ballX, ballY, velocityX, velocityY, tone, volume, phase;
    public ArcadeGame(GameKind kind, int seed, float gameSpeed = .5f) { Kind = kind; random = new(seed); SetSpeed(gameSpeed); Reset(); }
    public void Reset()
    {
        Tick = 0; Score = 0; CaughtThisFrame = false; player = Width / 2f; volume = 0; phase = 0; Spawn(); Render(); Array.Clear(Audio);
        RewardTarget = Kind == GameKind.Catch ? RewardTeachingTarget.Catch(MathF.Abs(player - ballX), 0) : RewardTeachingTarget.Signed(0);
    }
    private void Spawn()
    {
        ballX = random.Next(2, Width - 2); ballY = 1;
        velocityX = Kind == GameKind.Rally ? (random.Next(2) == 0 ? -.32f : .32f) : 0;
        velocityY = Kind == GameKind.Rally ? .48f : .6f;
    }
    public float Step(Buttons buttons)
    {
        if (Done) throw new InvalidOperationException("Reset a finished episode before stepping.");
        CaughtThisFrame = false;
        float oldDistance = MathF.Abs(player - ballX);
        if (buttons.HasFlag(Buttons.Left)) player -= .8f * GameSpeed;
        if (buttons.HasFlag(Buttons.Right)) player += .8f * GameSpeed;
        player = Math.Clamp(player, 2, Width - 3);
        float distance = MathF.Abs(player - ballX);
        // Training rewards belong to the environment; they are not part of the sensory input.
        float reward = (oldDistance - distance) * (Kind == GameKind.Dodge ? -.055f : .055f);
        ballX += velocityX * GameSpeed; ballY += velocityY * GameSpeed;
        if (ballX < 1 || ballX > Width - 2) { velocityX = -velocityX; ballX = Math.Clamp(ballX, 1, Width - 2); }
        if (ballY < 1) { velocityY = MathF.Abs(velocityY); ballY = 1; }
        float teachingDistance = MathF.Abs(player - ballX); bool? outcome = null;
        if (ballY >= Height - 3)
        {
            bool hit = MathF.Abs(player - ballX) <= 2.4f;
            bool success = Kind == GameKind.Dodge ? !hit : hit;
            outcome = success; CaughtThisFrame = Kind == GameKind.Catch && hit;
            reward += success ? 1 : -1; Score += success ? 1 : -1;
            tone = success ? 220 : 110; volume = .8f;
            if (Kind == GameKind.Rally && hit) { ballY = Height - 3.1f; velocityY = -MathF.Abs(velocityY); }
            else Spawn();
        }
        RewardTarget = Kind == GameKind.Catch ? RewardTeachingTarget.Catch(teachingDistance, reward, outcome) : RewardTeachingTarget.Signed(reward);
        Tick++; Render();
        for (int i = 0; i < AudioSize; i++) { phase += 2 * MathF.PI * tone / SampleRate; Audio[i] = volume * MathF.Sin(phase); volume *= .987f; }
        phase %= 2 * MathF.PI;
        return reward;
    }
    private void Render()
    {
        Array.Clear(Pixels);
        for (int x = 0; x < Width; x++) { Pixels[x] = .12f; Pixels[(Height - 1) * Width + x] = .12f; }
        for (int y = 0; y < Height; y++) { Pixels[y * Width] = .12f; Pixels[y * Width + Width - 1] = .12f; }
        int px = (int)MathF.Round(player);
        for (int dx = -2; dx <= 2; dx++) Pixels[(Height - 2) * Width + Math.Clamp(px + dx, 0, Width - 1)] = .7f;
        int bx = Math.Clamp((int)MathF.Round(ballX), 0, Width - 1), by = Math.Clamp((int)MathF.Round(ballY), 0, Height - 1);
        Pixels[by * Width + bx] = 1;
        if (by + 1 < Height - 2 && Kind == GameKind.Dodge) Pixels[(by + 1) * Width + bx] = .9f;
    }
}
