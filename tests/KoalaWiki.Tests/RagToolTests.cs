using System.Linq;
using System.Text.Json.Nodes;
using KoalaWiki.Tools;
using Mem0.NET;
using Xunit;

namespace KoalaWiki.Tests;

public class RagToolTests
{
    [Fact]
    public async Task SearchAsync_WithMultipleWarehouses_MergesResults()
    {
        var capturedRequests = new List<SearchRequest>();
        var responses = new Dictionary<string, object?>
        {
            ["repo-a"] = new { hits = new[] { "A" } },
            ["repo-b"] = new { hits = new[] { "B" } }
        };

        await using var rag = new RagTool(new[] { "repo-a", "repo-b" }, request =>
        {
            capturedRequests.Add(request);
            return Task.FromResult(responses[request.UserId]);
        });

        var json = await rag.SearchAsync("test query", limit: 3, minRelevance: 0.5);
        var node = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(2, node["totalRepositories"]!.GetValue<int>());
        Assert.Equal("test query", node["query"]!.GetValue<string>());
        var results = node["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Contains(results, item =>
            item!["warehouseId"]!.GetValue<string>() == "repo-a" &&
            item["result"]!["hits"]![0]!.GetValue<string>() == "A");
        Assert.Contains(results, item =>
            item!["warehouseId"]!.GetValue<string>() == "repo-b" &&
            item["result"]!["hits"]![0]!.GetValue<string>() == "B");
        Assert.Equal(new[] { "repo-a", "repo-b" }, capturedRequests.Select(r => r.UserId).ToArray());
    }

    [Fact]
    public async Task SearchAsync_WithSingleWarehouse_ReturnsOriginalPayload()
    {
        await using var rag = new RagTool(new[] { "solo" }, request =>
        {
            return Task.FromResult<object?>(new { hits = new[] { request.UserId } });
        });

        var json = await rag.SearchAsync("solo-query");
        var node = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("solo", node["hits"]![0]!.GetValue<string>());
    }
}
