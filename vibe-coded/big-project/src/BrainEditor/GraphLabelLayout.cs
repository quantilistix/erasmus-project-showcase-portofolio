using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public static class GraphLabelLayout
{
    /// <summary>Keep short module names near their desired anchors while avoiding other names.</summary>
    public static GuiRect Place(GuiRect desired,GuiRect bounds,IReadOnlyList<GuiRect> occupied)
    {
        GuiRect best=desired;float score=float.PositiveInfinity;
        for(int row=-8;row<=8;row++)for(int column=-2;column<=2;column++)
        {
            var candidate=desired with
            {
                X=Math.Clamp(desired.X+column*(desired.Width*.5f+6),bounds.Left,Math.Max(bounds.Left,bounds.Right-desired.Width)),
                Y=Math.Clamp(desired.Y+row*21,bounds.Top,Math.Max(bounds.Top,bounds.Bottom-desired.Height))
            };
            float penalty=0;
            foreach(var other in occupied)
            {
                var overlap=candidate.Inset(-2).Intersect(other);
                penalty+=overlap.Width*overlap.Height;
            }
            float dx=candidate.X-desired.X,dy=candidate.Y-desired.Y;
            float value=penalty*100000+dx*dx+dy*dy;
            if(value<score){score=value;best=candidate;}
        }
        return best;
    }
}
