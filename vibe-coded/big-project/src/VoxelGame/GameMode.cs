namespace VoxelGame;


/// <summary>How a world is played, chosen when it is made.</summary>
internal enum GameMode
{
    /// <summary>
    /// Unlimited items, from a menu of all of them; flight; invulnerability, which can be turned
    /// off; and blocks broken in one hit, which can be turned off. Worlds saved before there
    /// were game modes are played in it, as they had all of that.
    /// </summary>
    Creative,

    /// <summary>Start with nothing; mine with tools, craft better ones, never fly, and get hurt.</summary>
    Survival
}
