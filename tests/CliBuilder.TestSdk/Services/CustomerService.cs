using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

public class CustomerService
{
    public CustomerService(string apiKey) { }

    public Task<Customer> CreateAsync(CreateCustomerOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<Customer> GetAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<List<Customer>> ListAsync(int limit = 10, string? cursor = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    // Second overload — adapter should pick richest parameter set, emit CB203
    public Task<Customer> CreateAsync(CreateCustomerOptions options, RequestOptions requestOptions, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    // Streaming return type
    public IAsyncEnumerable<Customer> StreamAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    // Dictionary return type
    public Task<Dictionary<string, object>> GetMetadataAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    // Non-async overload — collides with GetAsync after Async stripping
    public Customer Get(string id)
        => throw new NotImplementedException();
}
