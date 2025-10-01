using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using KoalaWiki.Domains;
using KoalaWiki.Domains.Warehouse;
using KoalaWiki.Infrastructure;
using KoalaWiki.KoalaWarehouse;
using KoalaWiki.Mem0;
using Microsoft.Extensions.Logging.Abstractions;
using Mem0.NET;
using Xunit;

namespace KoalaWiki.Tests;

public class Mem0ChunkUploaderTests
{
    private const int ChunkSize = 800 * 1024;

    [Fact]
    public async Task ProcessAsync_UploadsAllChunksWithMetadata()
    {
        using var tempDirectory = new TempDirectory();
        var filePath = Path.Combine(tempDirectory.DirectoryPath, "chunked.cs");
        var contentBuilder = new StringBuilder();
        contentBuilder.Append('B', ChunkSize);
        contentBuilder.Append("// tail");
        var fullContent = contentBuilder.ToString();
        File.WriteAllText(filePath, fullContent);

        var pathInfos = DocumentsHelper.GetCatalogueFiles(tempDirectory.DirectoryPath)
            .Where(info => info.Path == filePath)
            .OrderBy(info => info.ChunkIndex)
            .ToList();

        var document = new Document
        {
            Id = Guid.NewGuid().ToString(),
            GitPath = tempDirectory.DirectoryPath,
            WarehouseId = Guid.NewGuid().ToString()
        };

        var warehouse = new Warehouse
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Warehouse",
            Description = string.Empty,
            Address = string.Empty
        };

        var fakeClient = new FakeMem0ClientAdapter();
        var systemPrompt = "system";

        await Mem0ChunkUploader.ProcessAsync(fakeClient, pathInfos, document, warehouse, systemPrompt,
            CancellationToken.None, NullLogger.Instance);

        var orderedCalls = fakeClient.Calls
            .OrderBy(call => (int)call.Metadata["chunkIndex"])
            .ToList();

        Assert.Equal(pathInfos.Count, orderedCalls.Count);

        for (var i = 0; i < pathInfos.Count; i++)
        {
            var call = orderedCalls[i];
            var chunk = pathInfos[i];

            Assert.Equal(warehouse.Id, call.UserId);
            Assert.Equal("procedural_memory", call.MemoryType);
            Assert.Equal(systemPrompt, call.Messages[0].Content);
            Assert.Equal(chunk.ChunkIndex, (int)call.Metadata["chunkIndex"]);
            Assert.Equal(chunk.ChunkCount, (int)call.Metadata["chunkCount"]);
            Assert.Equal(chunk.ChunkOffset, (long)call.Metadata["chunkOffset"]);
            Assert.Equal(chunk.ChunkLength, (int)call.Metadata["chunkLength"]);

            var userMessage = call.Messages[1].Content;
            Assert.Contains(Path.GetFileName(filePath), userMessage);
            var expectedChunkContent = await Mem0ChunkUploader.ReadChunkContentAsync(chunk, CancellationToken.None);
            Assert.Contains(expectedChunkContent, userMessage);
        }
    }

    private sealed class FakeMem0ClientAdapter : IMem0ClientAdapter
    {
        public List<CallRecord> Calls { get; } = new();

        public Task AddAsync(IList<Message> messages, string? userId, IDictionary<string, object>? metadata,
            string? memoryType, CancellationToken cancellationToken)
        {
            Calls.Add(new CallRecord(messages.ToList(), userId, metadata ?? new Dictionary<string, object>(), memoryType));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed record CallRecord(List<Message> Messages, string? UserId, IDictionary<string, object> Metadata,
        string? MemoryType);
}
