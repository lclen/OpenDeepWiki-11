using System.Linq;
using KoalaWiki.Infrastructure;
using KoalaWiki.KoalaWarehouse;
using Xunit;

namespace KoalaWiki.Tests;

public class DocumentsHelperTests
{
    private const int ChunkSize = 800 * 1024;

    [Fact]
    public void ScanDirectory_SplitsLargeFilesIntoMultipleChunks()
    {
        using var tempDirectory = new TempDirectory();
        var largeFilePath = Path.Combine(tempDirectory.DirectoryPath, "large.txt");
        var largeContent = new string('A', ChunkSize + 200);
        File.WriteAllText(largeFilePath, largeContent);

        var files = DocumentsHelper.GetCatalogueFiles(tempDirectory.DirectoryPath)
            .Where(info => info.Path == largeFilePath)
            .OrderBy(info => info.ChunkIndex)
            .ToList();

        Assert.Equal(2, files.Count);

        var firstChunk = files[0];
        var secondChunk = files[1];

        Assert.Equal(0, firstChunk.ChunkIndex);
        Assert.Equal(2, firstChunk.ChunkCount);
        Assert.Equal(0, firstChunk.ChunkOffset);
        Assert.Equal(ChunkSize, firstChunk.ChunkLength);
        Assert.Equal(largeContent.Length, firstChunk.Size);

        Assert.Equal(1, secondChunk.ChunkIndex);
        Assert.Equal(2, secondChunk.ChunkCount);
        Assert.Equal(ChunkSize, secondChunk.ChunkOffset);
        Assert.Equal(200, secondChunk.ChunkLength);
        Assert.Equal(largeContent.Length, secondChunk.Size);
    }

    [Fact]
    public void ScanDirectory_UsesSingleChunkForSmallFiles()
    {
        using var tempDirectory = new TempDirectory();
        var filePath = Path.Combine(tempDirectory.DirectoryPath, "small.txt");
        const string content = "hello";
        File.WriteAllText(filePath, content);

        var info = DocumentsHelper.GetCatalogueFiles(tempDirectory.DirectoryPath)
            .Single(x => x.Path == filePath);

        Assert.Equal(0, info.ChunkIndex);
        Assert.Equal(1, info.ChunkCount);
        Assert.Equal(0, info.ChunkOffset);
        Assert.Equal(content.Length, info.ChunkLength);
        Assert.Equal(content.Length, info.Size);
    }

}
