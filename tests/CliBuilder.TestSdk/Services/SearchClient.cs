using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

/// <summary>
/// Simulates multi-arg constructor pattern (like OpenAI ChatClient).
/// Has both single-arg and multi-arg constructors — adapter should prefer richest.
/// </summary>
public class SearchClient
{
    public SearchClient(ApiKeyCredential credential) { }
    public SearchClient(string index, ApiKeyCredential credential) { }

    public Task<Product> SearchAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult(new Product { Id = "result_001", Name = $"Result for: {query}" });
}
