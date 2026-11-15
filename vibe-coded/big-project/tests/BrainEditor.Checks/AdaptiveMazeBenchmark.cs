using ArcadeLab;
using System.Numerics;
using System.Text.Json;

internal static class AdaptiveMazeBenchmark
{
    internal static void Run()
    {
        // Fixed small mazes isolate online policy learning. No teacher, position or depth inputs.
        var layouts=new[]
        {
            new MazeScenario(["#########","#.....E.#","#########"],new Vector2(1.5f,1.5f),0,[],Ammo:0,Reserve:0),
            new MazeScenario(["#######","#...###","###.###","###..E#","#######"],new Vector2(1.5f,1.5f),0,[],Ammo:0,Reserve:0)
        };
        foreach(int seed in new[]{17,42,91})
        {
            var learner=new MazeAdaptiveLearner(seed);float[] cortex=new float[8];
            (bool Completed,float Return,int Steps) Episode(MazeScenario scenario,bool training)
            {
                learner.ResetEpisode();var game=new MazeGame(seed,2,scenario);var rewards=new MazeAdaptiveRewards();int steps=0;
                for(;steps<600;steps++)
                {
                    game.Step(learner.Choose(game,cortex,training),true);
                    bool terminal=game.Completed||game.Done||steps==599;
                    learner.Observe(game,cortex,rewards.Observe(game),terminal,training);
                    if(terminal){steps++;break;}
                }
                return(game.Completed,rewards.EpisodeReturn,steps);
            }
            var before=layouts.Select(m=>Episode(m,false)).ToArray();int trainedCompletions=0;
            for(int episode=0;episode<80;episode++)if(Episode(layouts[episode%layouts.Length],true).Completed)trainedCompletions++;
            var after=layouts.Select(m=>Episode(m,false)).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new {seed,trainingEpisodes=80,trainedCompletions,decisions=learner.Decisions,updates=learner.Updates,
                before=before.Select(r=>new {r.Completed,r.Return,r.Steps}),after=after.Select(r=>new {r.Completed,r.Return,r.Steps})}));
        }
    }
}
