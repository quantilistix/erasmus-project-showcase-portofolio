using GlmSharp;
using VoxelGame;

namespace VoxelTests;

/// <summary>Checks the rendered light application, including wall edges and room corners.</summary>
internal static class EnclosedRoomTests
{
    public static bool Run(GraphicsContext graphics,Renderer renderer,World world,string captures)
    {
        // A one-block shell crossing chunk boundaries on all three axes, including y=0.
        ivec3 min=new(12,-3,13), max=new(20,5,21);
        for(int z=min.z;z<=max.z;z++)
        for(int y=min.y;y<=max.y;y++)
        for(int x=min.x;x<=max.x;x++)
            if(x==min.x || x==max.x || y==min.y || y==max.y || z==min.z || z==max.z)
                world.Map.Grid[new ivec3(x,y,z)]=new Voxel(BlockType.Stone,0);
        world.Map.MeshDirty.UnionWith(world.Map.Grid.TakeChangedChunks());
        renderer.Video.AmbientBrightness=100;
        renderer.Video.DynamicShadows=true;
        int sequence=0;
        byte[] Capture(dvec3 direction)
        {
            Camera camera=new(new dvec3(16.37,1.21,17.61),direction.Normalized,Math.PI/2,640.0/480);
            renderer.Sync(world,camera.Position);
            renderer.WaitForLighting(world.Bodies,b=>(b.Position,b.Orientation));
            if(!graphics.BeginFrame(out Frame frame)) throw new InvalidOperationException("Room test frame unavailable");
            graphics.CapturePath=Path.Combine(captures,$"room-{sequence++}.ppm");
            string path=graphics.CapturePath;
            renderer.Draw(frame,camera,world.Bodies,b=>(b.Position,b.Orientation),world.Particles,[],null,[],[],false);
            graphics.EndFrame(frame);
            byte[] ppm=File.ReadAllBytes(path); int offset=0;
            for(int i=0;i<3;i++) offset=Array.IndexOf(ppm,(byte)'\n',offset)+1;
            return ppm[offset..];
        }
        bool passed=true;
        foreach(bool merged in new[] { false,true })
        {
            VoxelMesher.MergeRenderFaces=merged;
            renderer.Clear();
            foreach(var chunk in world.Map.Grid.Chunks) world.Map.MeshDirty.Add(chunk.Coordinate);
            // Compare the same rasterized surfaces with illumination disabled. Isolated
            // background samples at greedy-mesh T-junctions are a geometry issue, not GI.
            renderer.Video.AmbientBrightness=0;
            renderer.Video.Sun=false;
            byte[][] unlit=new byte[8][];
            for(int view=0;view<8;view++) unlit[view]=Capture(Direction(view));
            renderer.Video.AmbientBrightness=100;
            foreach(bool sun in new[] { false,true })
            {
                renderer.Video.Sun=sun;
                int lit=0, peak=0;
                // All eight corners: each view includes three interior faces and their joins.
                for(int view=0;view<8;view++)
                {
                    byte[] pixels=Capture(Direction(view));
                    if(!merged && !sun && view==0)
                    {
                        var chunks=renderer.CaptureLighting().ToDictionary(c=>c.Coordinate);
                        int nonzero=0;
                        for(int az=min.z+1;az<max.z;az++)
                        for(int ay=min.y+1;ay<max.y;ay++)
                        for(int ax=min.x+1;ax<max.x;ax++)
                        {
                            var chunk=chunks[new ivec3(ax,ay,az)>>4];
                            int index=(ax&15)+16*(ay&15)+256*(az&15);
                            if(chunk.Rgb[chunk.Rgb.Length==1 ? 0 : index]!=0) nonzero++;
                        }
                        Console.WriteLine($"   sealed room's GPU air grid: {nonzero} nonzero cells; {(nonzero==0 ? "PASS" : "FAIL")}");
                        passed &= nonzero==0;
                    }
                    for(int i=0;i<pixels.Length;i+=3)
                    {
                        int brightest=Math.Max(Math.Abs(pixels[i]-unlit[view][i]),
                            Math.Max(Math.Abs(pixels[i+1]-unlit[view][i+1]),Math.Abs(pixels[i+2]-unlit[view][i+2])));
                        peak=Math.Max(peak,brightest);
                        if(brightest!=0) lit++;
                    }
                }
                bool black=lit==0;
                Console.WriteLine($"   sealed one-block room, merged={merged}, sun={sun}: {lit} illuminated surface pixels, peak difference {peak}; {(black ? "PASS" : "FAIL")}");
                passed &= black;
            }
        }
        // Positive control: a real opening must still illuminate the room.
        for(int y=0;y<3;y++) for(int x=15;x<18;x++)
            world.Map.Grid[new ivec3(x,y,max.z)]=new Voxel(BlockType.Glass,0);
        world.Map.MeshDirty.UnionWith(world.Map.Grid.TakeChangedChunks());
        byte[] opened=Capture(new dvec3(1,1,-1));
        int visible=0;
        for(int i=0;i<opened.Length;i+=3) if(opened[i]>10 || opened[i+1]>10 || opened[i+2]>10) visible++;
        bool lights=visible>5000;
        Console.WriteLine($"   glass skylight illuminates interior surfaces: {visible} lit pixels; {(lights ? "PASS" : "FAIL")}");
        passed &= lights;
        if(passed) Directory.Delete(captures,true);
        else Console.WriteLine($"   Room images: {captures}");
        return passed;
    }

    private static dvec3 Direction(int corner) => new((corner&1)==0 ? -1 : 1,(corner&2)==0 ? -1 : 1,(corner&4)==0 ? -1 : 1);
}
