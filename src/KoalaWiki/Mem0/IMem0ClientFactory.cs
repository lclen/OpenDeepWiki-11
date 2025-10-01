using System.Collections.Generic;
using System.Threading;
using Mem0.NET;

namespace KoalaWiki.Mem0;

public interface IMem0ClientAdapter : IAsyncDisposable
{
    Task AddAsync(IList<Message> messages, string? userId, IDictionary<string, object>? metadata,
        string? memoryType, CancellationToken cancellationToken);
}

public interface IMem0ClientFactory
{
    IMem0ClientAdapter CreateClient();
}
