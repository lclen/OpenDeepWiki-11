using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mem0.NET;

namespace KoalaWiki.Tools;

public class RagTool : IDisposable, IAsyncDisposable
{
    private readonly IReadOnlyList<string> _warehouseIds;
    private readonly Mem0Client? _mem0Client;
    private readonly Func<SearchRequest, Task<object?>> _searchExecutor;

    public RagTool(string warehouseId)
        : this(new[] { warehouseId })
    {
    }

    public RagTool(IEnumerable<string> warehouseIds, Func<SearchRequest, Task<object?>>? searchExecutor = null)
    {
        if (warehouseIds == null)
        {
            throw new ArgumentNullException(nameof(warehouseIds));
        }

        var distinctIds = warehouseIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctIds.Count == 0)
        {
            throw new ArgumentException("At least one warehouseId must be provided", nameof(warehouseIds));
        }

        _warehouseIds = distinctIds;

        if (searchExecutor != null)
        {
            _searchExecutor = searchExecutor;
        }
        else
        {
            _mem0Client = new Mem0Client(OpenAIOptions.Mem0ApiKey, OpenAIOptions.Mem0Endpoint, null, null,
                new HttpClient()
                {
                    Timeout = TimeSpan.FromMinutes(600),
                    DefaultRequestHeaders =
                    {
                        UserAgent = { new ProductInfoHeaderValue("KoalaWiki", "1.0") }
                    }
                });

            _searchExecutor = async request => await _mem0Client.SearchAsync(request);
        }
    }


    [KernelFunction("rag_search"), Description("Search and retrieve relevant code or documentation content from the current repository index using specific keywords.")]
    public async Task<string> SearchAsync(
        [Description("Detailed description of the code or documentation you need. Specify whether you're looking for a function, class, method, or specific documentation. Be as specific as possible to improve search accuracy.")]
        string query,
        [Description("Number of search results to return. Default is 5. Increase for broader coverage or decrease for focused results.")]
        int limit = 5,
        [Description("Minimum relevance threshold for vector search results, ranging from 0 to 1. Default is 0.3. Higher values (e.g., 0.7) return more precise matches, while lower values provide more varied results.")]
        double minRelevance = 0.3)
    {
        var responses = await Task.WhenAll(
            _warehouseIds.Select(id => ExecuteSearchAsync(id, query, limit, minRelevance)));

        if (responses.Length == 1)
        {
            return JsonSerializer.Serialize(responses[0].Result, JsonSerializerOptions.Web);
        }

        var resultsArray = new JsonArray();
        foreach (var response in responses)
        {
            JsonNode? resultNode = null;
            if (response.Result != null)
            {
                resultNode = JsonSerializer.SerializeToNode(response.Result, JsonSerializerOptions.Web);
            }

            var entry = new JsonObject
            {
                ["warehouseId"] = response.WarehouseId,
            };

            if (resultNode != null)
            {
                entry["result"] = resultNode;
            }

            resultsArray.Add(entry);
        }

        var payload = new JsonObject
        {
            ["query"] = query,
            ["limit"] = limit,
            ["minRelevance"] = minRelevance,
            ["totalRepositories"] = responses.Length,
            ["results"] = resultsArray
        };

        return payload.ToJsonString(JsonSerializerOptions.Web);
    }

    private async Task<(string WarehouseId, object? Result)> ExecuteSearchAsync(string warehouseId, string query,
        int limit, double minRelevance)
    {
        var request = new SearchRequest()
        {
            Query = query,
            UserId = warehouseId,
            Threshold = minRelevance,
            Limit = limit,
        };

        var result = await _searchExecutor(request);
        return (warehouseId, result);
    }

    public void Dispose()
    {
        _mem0Client?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
