namespace LmKitOmniApi.Application.Abstractions;

public interface IWebSearchService
{
    Task<string> SearchWebAsync(string query, int count = 5, CancellationToken ct = default);
}
