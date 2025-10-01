using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using KoalaWiki.Domains;
using KoalaWiki.Domains.Warehouse;
using KoalaWiki.KoalaWarehouse;
using Microsoft.Extensions.Logging;

namespace KoalaWiki.Mem0;

internal static class Mem0ChunkUploader
{
    public static async Task ProcessAsync(IMem0ClientAdapter client, IReadOnlyCollection<PathInfo> fileChunks,
        Document document, Warehouse warehouse, string systemPrompt, CancellationToken cancellationToken,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(fileChunks);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(warehouse);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            throw new ArgumentException("System prompt cannot be null or empty", nameof(systemPrompt));
        }

        if (fileChunks.Count == 0)
        {
            return;
        }

        var orderedChunks = fileChunks
            .OrderBy(chunk => chunk.ChunkIndex)
            .ToList();

        foreach (var chunk in orderedChunks)
        {
            var content = await ReadChunkContentAsync(chunk, cancellationToken);

            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning("文件 {File} 分片 {Index}/{Count} 内容为空，跳过", chunk.Path,
                    chunk.ChunkIndex + 1, chunk.ChunkCount);
                continue;
            }

            string relativePath;
            if (!string.IsNullOrWhiteSpace(document.GitPath))
            {
                relativePath = Path.GetRelativePath(document.GitPath, chunk.Path)
                    .Replace('\\', '/');
            }
            else
            {
                relativePath = chunk.Path;
            }

            var userContent = $"```{relativePath}\n{content}\n```";

            var messages = new List<Mem0.NET.Message>
            {
                new()
                {
                    Role = "system",
                    Content = systemPrompt
                },
                new()
                {
                    Role = "user",
                    Content = userContent
                }
            };

            var metadata = BuildChunkMetadata(chunk, document.Id);
            metadata["fileName"] = chunk.Name;
            metadata["filePath"] = chunk.Path;
            metadata["fileType"] = chunk.Type;
            metadata["type"] = "code";

            await client.AddAsync(messages, warehouse.Id, metadata, "procedural_memory", cancellationToken);
        }
    }

    public static Dictionary<string, object> BuildChunkMetadata(PathInfo chunk, string documentId)
    {
        return new Dictionary<string, object>
        {
            { "fileSize", chunk.Size },
            { "chunkIndex", chunk.ChunkIndex },
            { "chunkCount", chunk.ChunkCount },
            { "chunkOffset", chunk.ChunkOffset },
            { "chunkLength", chunk.ChunkLength },
            { "documentId", documentId }
        };
    }

    public static async Task<string> ReadChunkContentAsync(PathInfo chunk, CancellationToken cancellationToken)
    {
        var effectiveLength = chunk.ChunkLength;
        if (chunk.ChunkOffset + effectiveLength > chunk.Size)
        {
            effectiveLength = (int)Math.Max(0, chunk.Size - chunk.ChunkOffset);
        }

        if (effectiveLength <= 0)
        {
            return string.Empty;
        }

        if (chunk.ChunkOffset == 0 && effectiveLength >= chunk.Size)
        {
            return await File.ReadAllTextAsync(chunk.Path, cancellationToken);
        }

        await using var stream = new FileStream(chunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: Math.Max(4096, effectiveLength), useAsync: true);
        stream.Seek(chunk.ChunkOffset, SeekOrigin.Begin);

        var buffer = new byte[effectiveLength];
        var totalRead = 0;

        while (totalRead < effectiveLength)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, effectiveLength - totalRead),
                cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return Encoding.UTF8.GetString(buffer, 0, totalRead);
    }
}
