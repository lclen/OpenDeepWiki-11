using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Threading;
using KoalaWiki.Options;

namespace KoalaWiki.Mem0;

public class DefaultMem0ClientFactory(IHttpClientFactory httpClientFactory) : IMem0ClientFactory
{
    public IMem0ClientAdapter CreateClient()
    {
        var httpClient = httpClientFactory.CreateClient(nameof(Mem0Rag));
        httpClient.Timeout = TimeSpan.FromMinutes(600);
        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KoalaWiki", "1.0"));
        }

        var client = new Mem0.NET.Mem0Client(OpenAIOptions.Mem0ApiKey, OpenAIOptions.Mem0Endpoint, null, null, httpClient);
        return new Mem0ClientAdapter(client, httpClient);
    }

    private sealed class Mem0ClientAdapter : IMem0ClientAdapter
    {
        private readonly Mem0.NET.Mem0Client _client;
        private readonly HttpClient _httpClient;

        public Mem0ClientAdapter(Mem0.NET.Mem0Client client, HttpClient httpClient)
        {
            _client = client;
            _httpClient = httpClient;
        }

        public Task AddAsync(IList<Mem0.NET.Message> messages, string? userId, IDictionary<string, object>? metadata,
            string? memoryType, CancellationToken cancellationToken)
        {
            return _client.AddAsync(messages, userId: userId, metadata: metadata, memoryType: memoryType,
                cancellationToken: cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            switch (_client)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            _httpClient.Dispose();
        }
    }
}
