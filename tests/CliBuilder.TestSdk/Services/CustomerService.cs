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

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    // Non-async overload — collides with GetAsync after Async stripping
    public Customer Get(string id)
        => throw new NotImplementedException();
}
