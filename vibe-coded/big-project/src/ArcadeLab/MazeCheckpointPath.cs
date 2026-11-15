namespace ArcadeLab;

internal static class MazeCheckpointPath
{
    internal static string ResolveDefault(string root)
    {
        return Path.Combine(root,"artifacts","predictive-maze");
    }
    internal static string RepositoryRoot()
    {
        foreach(string start in new[]{AppContext.BaseDirectory,Directory.GetCurrentDirectory()})
            for(var dir=new DirectoryInfo(start);dir is not null;dir=dir.Parent)
                if((File.Exists(Path.Combine(dir.FullName,"Tester.sln"))||File.Exists(Path.Combine(dir.FullName,"ArtificialBrain.sln"))))return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
