using GlmSharp;
using VoxelGame;

namespace VoxelTests;

/// <summary>Rendered visibility compared with analytic rays, independently of shadow-map code.</summary>
internal static class ShadowTests
{
    public static bool Run(GraphicsContext graphics,Renderer renderer,World world,string captures)
    {
        for(int y=0;y<16;y++) for(int x=0;x<16;x++)
            world.Map.Grid[new ivec3(x,y,0)]=new Voxel(BlockType.Stone,0);
        world.Map.Grid[new ivec3(8,8,4)]=new Voxel(BlockType.Furnace,0,Lit:true);
        world.ApplyTopologyChanges();
        Camera camera=new(new dvec3(8,-4,15),new dvec3(0,12,-14).Normalized,Math.PI/3,640.0/480);
        renderer.Video.DynamicShadows=true;
        renderer.Video.AmbientBrightness=0;
        int sequence=0;
        byte[] Capture(int probe)
        {
            renderer.ShadowProbe=probe;
            renderer.Sync(world,camera.Position);
            renderer.WaitForLighting(world.Bodies,b=>(b.Position,b.Orientation));
            if(!graphics.BeginFrame(out Frame frame)) throw new InvalidOperationException("Shadow test frame unavailable");
            string path=Path.Combine(captures,$"shadow-{sequence++}-{probe}.ppm"); graphics.CapturePath=path;
            renderer.Draw(frame,camera,world.Bodies,b=>(b.Position,b.Orientation),world.Particles,[],null,[],[],false);
            graphics.EndFrame(frame);
            byte[] ppm=File.ReadAllBytes(path); int offset=0;
            for(int i=0;i<3;i++) offset=Array.IndexOf(ppm,(byte)'\n',offset)+1;
            return ppm[offset..];
        }
        byte[] point=Capture(2);
        mat4 inverse=camera.ViewProjection().Inverse;
        int tested=0, dark=0;
        for(int y=0;y<480;y++) for(int x=0;x<640;x++)
        {
            vec4 h=inverse*new vec4(2f*(x+.5f)/640-1,2f*(y+.5f)/480-1,.5f,1);
            dvec3 ray=new dvec3(h.xyz/h.w)-camera.Position;
            if(ray.z>=0) continue;
            double t=(1-camera.Position.z)/ray.z;
            dvec3 p=camera.Position+t*ray;
            if(p.x<1 || p.x>15 || p.y<1 || p.y>15 || HitsBox(camera.Position,ray,t)) continue;
            tested++; if(point[(x+640*y)*3]<250) dark++;
        }
        bool passed=tested>10000 && dark==0;
        Console.WriteLine($"   furnace receiver plane: {dark}/{tested} spurious dark floor pixels; {(passed ? "PASS" : "FAIL")}");
        byte[] sun=Capture(1);
        mat4 view=camera.ViewProjection();
        dvec3 direction=new dvec3(.45,.3,.84);
        double expectedArea=0, actualArea=0;
        dvec2 expectedCenter=dvec2.Zero, actualCenter=dvec2.Zero;
        for(double y=4;y<11;y+=.025) for(double x=4;x<11;x+=.025)
        {
            dvec3 p=new(x,y,1);
            double expected=HitsBox(p,direction,double.PositiveInfinity) ? 1 : 0;
            vec4 h=view*new vec4((vec3)p,1);
            int px=(int)((h.x/h.w*.5f+.5f)*640), py=(int)((h.y/h.w*.5f+.5f)*480);
            if(px<0 || px>=640 || py<0 || py>=480) continue;
            double encoded=sun[(px+640*py)*3]/255.0;
            double actual=1-(encoded<=.04045 ? encoded/12.92 : Math.Pow((encoded+.055)/1.055,2.4));
            expectedArea+=expected; actualArea+=actual;
            expectedCenter+=new dvec2(x,y)*expected; actualCenter+=new dvec2(x,y)*actual;
        }
        double displacement=(actualCenter/actualArea-expectedCenter/expectedArea).Length;
        double areaError=Math.Abs(actualArea/expectedArea-1);
        bool aligned=double.IsFinite(displacement) && displacement<.025 && areaError<.05;
        Console.WriteLine($"   sun footprint vs analytic rays: centroid error {displacement:F4} m, area error {areaError:P2}; {(aligned ? "PASS" : "FAIL")}");
        passed &= aligned;
        // Exercise another receiver normal and cube-face seams, away from floor contact.
        for(int z=1;z<10;z++) for(int x=0;x<16;x++)
            world.Map.Grid[new ivec3(x,15,z)]=new Voxel(BlockType.Stone,0);
        world.ApplyTopologyChanges();
        byte[] wall=Capture(2);
        tested=dark=0;
        for(int y=0;y<480;y++) for(int x=0;x<640;x++)
        {
            vec4 h=inverse*new vec4(2f*(x+.5f)/640-1,2f*(y+.5f)/480-1,.5f,1);
            dvec3 ray=new dvec3(h.xyz/h.w)-camera.Position;
            if(ray.y<=0) continue;
            double t=(15-camera.Position.y)/ray.y;
            dvec3 p=camera.Position+t*ray;
            if(p.x<1 || p.x>15 || p.z<2 || p.z>9 || HitsBox(camera.Position,ray,t)) continue;
            tested++; if(wall[(x+640*y)*3]<250) dark++;
        }
        bool wallPass=tested>1000 && dark==0;
        Console.WriteLine($"   furnace wall receiver: {dark}/{tested} spurious dark pixels; {(wallPass ? "PASS" : "FAIL")}");
        passed &= wallPass;
        renderer.ShadowProbe=0;
        if(passed) Directory.Delete(captures,true);
        else Console.WriteLine($"   Shadow images: {captures}");
        return passed;
    }

    private static bool HitsBox(dvec3 origin,dvec3 ray,double limit)
    {
        dvec3 min=new(8,8,4), max=new(9,9,5);
        double enter=0, leave=limit;
        for(int axis=0;axis<3;axis++)
        {
            if(Math.Abs(ray[axis])<1e-12)
            { if(origin[axis]<min[axis] || origin[axis]>max[axis]) return false; continue; }
            double a=(min[axis]-origin[axis])/ray[axis], b=(max[axis]-origin[axis])/ray[axis];
            enter=Math.Max(enter,Math.Min(a,b)); leave=Math.Min(leave,Math.Max(a,b));
            if(enter>leave) return false;
        }
        return leave>0 && enter<limit;
    }
}
