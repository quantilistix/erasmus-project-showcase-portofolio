

namespace MYT.IKit;


public interface IAsset
{
}

public interface IAssetLoader<TAsset>
    where TAsset : IAsset
{
    
    TAsset Load(string filename);

}

public interface IAssetSaver<TAsset>
    where TAsset : IAsset
{
    void Save(TAsset asset, string filename);
}