using System.Numerics;

namespace ArcadeLab;

public sealed partial class MazeGame
{
    public const int TactileCount=16;
    public const float TactileTouch=.05f,TactilePush=.25f,TactileShotMinimum=.75f,TactileShotHalfLife=.35f;
    public float[] Tactile { get; }=new float[TactileCount];
    private readonly float[] tactileTrauma=new float[TactileCount];
    private readonly Random tactileRandom=new(7919);
    private void UpdateTactile(Vector2 blocked,float damage)
    {
        float fade=MathF.Pow(.5f,Speed/(60*TactileShotHalfLife));
        for(int i=0;i<TactileCount;i++)
        {
            tactileTrauma[i]*=fade;
            float angle=heading+2*MathF.PI*i/TactileCount;
            var direction=new Vector2(MathF.Cos(angle),MathF.Sin(angle));
            float contact=Tile(position+direction*.23f)=='#'?TactileTouch:0;
            if(blocked.LengthSquared()>0&&Vector2.Dot(Vector2.Normalize(blocked),direction)>MathF.Cos(MathF.PI/TactileCount+.01f))contact=TactilePush;
            Tactile[i]=Math.Max(contact,tactileTrauma[i]);
        }
        if(damage>0)
        {
            int direction=tactileRandom.Next(TactileCount);
            tactileTrauma[direction]=Tactile[direction]=TactileShotMinimum+(float)tactileRandom.NextDouble()*(1-TactileShotMinimum);
        }
    }
    private void ResetTactile(){Array.Clear(Tactile);Array.Clear(tactileTrauma);UpdateTactile(Vector2.Zero,0);}
}
