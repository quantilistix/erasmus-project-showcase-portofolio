using System.Numerics;

namespace ArcadeLab;

public sealed partial class MazeGame
{
    public const int PresentationWidth = 192, PresentationHeight = 128;
    /// <summary>Observer-only color raster sampled directly from geometry. Does not alter neural pixels or game state.</summary>
    public void RenderPresentation(Span<uint> destination, int width, int height, bool overview = false)
    {
        if (width < 1 || height < 1 || destination.Length != width * height) throw new ArgumentException("Presentation shape.");
        if(overview) { RenderTopView(destination,width,height); return; }
        Vector2 forward = new(MathF.Cos(heading), MathF.Sin(heading)), side = new(-forward.Y, forward.X);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            float lateral = ((x + .5f) / width - .5f) * 8;
            float ahead = (15f - (y + .5f) * 16 / height) / 3;
            Vector2 world = overview ? new((x + .5f) * MapWidth / width, (y + .5f) * MapHeight / height) : position + side * lateral + forward * ahead;
            uint color = 0x111B2B;
            bool inView = ahead >= -.4f && MathF.Abs(lateral) <= .65f + MathF.Max(0, ahead) * .9f;
            if (overview || (inView && PresentationVisible(world)))
            {
                color = SampleSceneColor(world, forward);
                if (!overview) color = ShadeColor(color, DistanceBrightness(Vector2.Distance(world, position)));
            }
            destination[y * width + x] = color;
        }
    }
    /// <summary>World light attenuation, shared by the RGB sensor and observer camera.</summary>
    public static float DistanceBrightness(float distance)
    {
        if (!float.IsFinite(distance) || distance<0) throw new ArgumentOutOfRangeException(nameof(distance));
        return 1/(1+.12f*distance*distance);
    }
    private static uint ShadeColor(uint color,float brightness)
    {
        uint r=(uint)(((color>>16)&255)*brightness),g=(uint)(((color>>8)&255)*brightness),b=(uint)((color&255)*brightness);
        return r<<16|g<<8|b;
    }
    private uint SampleSceneColor(Vector2 world,Vector2 forward)
    {
        uint color;
        float fx = world.X - MathF.Floor(world.X), fy = world.Y - MathF.Floor(world.Y);
        Vector2 centre = new(MathF.Floor(world.X) + .5f, MathF.Floor(world.Y) + .5f);
        char tile = Tile(world);
        color = ((int)MathF.Floor(world.X) + (int)MathF.Floor(world.Y)) % 2 == 0 ? 0x253549u : 0x293A4Eu;
        if (tile == '#')
        {
            bool edge = fx < .035f || fy < .035f || fx > .965f || fy > .965f;
            color = edge ? 0x8CABC2u : 0x536E89u;
            if (!edge && (fy < .10f || fx < .10f)) color = 0x647F98;
        }
        else if (tile == 'H')
        {
            var d = world - centre;
            color = Disc(color, 0x123D36, d.Length(), .33f);
            if ((MathF.Abs(d.X) < .075f && MathF.Abs(d.Y) < .24f) || (MathF.Abs(d.Y) < .075f && MathF.Abs(d.X) < .24f)) color = 0x6EF0B0;
        }
        else if (tile == 'A')
        {
            var d = Vector2.Abs(world - centre);
            if (d.X < .24f && d.Y < .27f) color = 0xC49239;
            if (d.X < .19f && d.Y < .21f) color = ((int)((world.X - centre.X + .24f) * 20) % 2 == 0) ? 0xFFE19Du : 0x70552Fu;
        }
        else if (tile == 'E')
        {
            var d = Vector2.Abs(world - centre);
            if (d.X < .42f && d.Y < .42f) color = d.X > .32f || d.Y > .32f ? 0x76EDE5u : 0x184D59u;
            if (MathF.Abs(world.X - centre.X) < .065f && d.Y < .25f) color = 0xC2FFEC;
        }
        foreach (var enemy in enemies)
        {
            Vector2 d = world - enemy.Position; float distance = d.Length();
            if (enemy.Health <= 0) { color = Disc(color, 0x67414C, distance, .21f); continue; }
            color = Disc(color, 0x281E2D, distance, .34f);
            color = Disc(color, 0xED6370, distance, .27f);
            if (distance < .18f) color = 0xFFB09A;
            Vector2 aim = Vector2.Normalize(position - enemy.Position + new Vector2(.00001f));
            if (Vector2.Distance(world, enemy.Position + aim * .25f) < .09f) color = 0x211D31;
            Vector2 right = new(-aim.Y, aim.X);
            if (Vector2.Distance(world, enemy.Position + aim * .11f + right * .085f) < .035f || Vector2.Distance(world, enemy.Position + aim * .11f - right * .085f) < .035f) color = 0x271D2D;
        }
        foreach (var projectile in projectiles)
        {
            Vector2 d = world - projectile.Position, direction = Vector2.Normalize(projectile.Velocity);
            float along = Math.Clamp(Vector2.Dot(d, direction), -.32f, 0);
            float distance = (d - along * direction).Length();
            color = Disc(color, projectile.Enemy ? 0xFFA861u : 0xFFF2B0u, distance, .045f);
        }
        color = Disc(color, 0x0E263B, Vector2.Distance(world, position), .23f);
        color = Disc(color, 0x6CCFF6, Vector2.Distance(world, position), .17f);
        color = Disc(color, 0xE0F8FF, Vector2.Distance(world, position + forward * .22f), .065f);
        return color;
    }
    private static uint Disc(uint background, uint foreground, float distance, float radius)
    {
        float coverage = Math.Clamp((radius - distance) / .035f + .5f, 0, 1);
        coverage = MathF.Round(coverage * 4) / 4;
        if (coverage <= 0) return background; if (coverage >= 1) return foreground;
        uint r = (uint)(((background >> 16) & 255) * (1 - coverage) + ((foreground >> 16) & 255) * coverage);
        uint g = (uint)(((background >> 8) & 255) * (1 - coverage) + ((foreground >> 8) & 255) * coverage);
        uint b = (uint)((background & 255) * (1 - coverage) + (foreground & 255) * coverage);
        return r << 16 | g << 8 | b;
    }
    private bool PresentationVisible(Vector2 target)
    {
        var (x, y) = Cell(position); var (tx, ty) = Cell(target); Vector2 delta = target - position;
        int sx = Math.Sign(delta.X), sy = Math.Sign(delta.Y);
        float dx = sx == 0 ? float.PositiveInfinity : MathF.Abs(1 / delta.X), dy = sy == 0 ? float.PositiveInfinity : MathF.Abs(1 / delta.Y);
        float mx = sx == 0 ? float.PositiveInfinity : (sx > 0 ? x + 1 - position.X : position.X - x) * dx;
        float my = sy == 0 ? float.PositiveInfinity : (sy > 0 ? y + 1 - position.Y : position.Y - y) * dy;
        for (int step = 0; step < 128; step++)
        {
            if (x == tx && y == ty) return true;
            if (Tile(new(x + .5f, y + .5f)) == '#') return false;
            if (mx < my) { x += sx; mx += dx; }
            else { y += sy; my += dy; }
        }
        return false;
    }
}
