namespace KoalaWiki.KoalaWarehouse;

public class PathInfo
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long Size { get; set; }
    public int ChunkIndex { get; set; }
    public int ChunkCount { get; set; }
    public long ChunkOffset { get; set; }
    public int ChunkLength { get; set; }
    public bool IsChunked => ChunkCount > 1;
}
