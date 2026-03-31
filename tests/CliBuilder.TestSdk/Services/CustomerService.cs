using System.Runtime.CompilerServices;
using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

public class CustomerService
{
    public CustomerService(string apiKey) { }

    public Task<Customer> CreateAsync(CreateCustomerOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(new Customer
        {
            Id = "cust_001",
            Email = options.Email,
            Name = options.Name,
            Status = options.InitialStatus ?? CustomerStatus.Active
        });

    public Task<Customer> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(new Customer
        {
            Id = id,
            Email = "test@example.com",
            Status = CustomerStatus.Active
        });

    public Task<List<Customer>> ListAsync(int limit = 10, string? cursor = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<Customer>
        {
            new() { Id = "cust_001", Email = "alice@test.com", Status = CustomerStatus.Active },
            new() { Id = "cust_002", Email = "bob@test.com", Status = CustomerStatus.Inactive }
        });

    // Second overload — adapter picks richest parameter set, emits CB203
    public Task<Customer> CreateAsync(CreateCustomerOptions options, RequestOptions requestOptions, CancellationToken cancellationToken = default)
        => Task.FromResult(new Customer
        {
            Id = "cust_001",
            Email = options.Email,
            Name = options.Name,
            Status = options.InitialStatus ?? CustomerStatus.Active
        });

    public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        => new(true);

    // Streaming return type
    public async IAsyncEnumerable<Customer> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new Customer { Id = "cust_s1", Email = "stream1@test.com", Status = CustomerStatus.Active };
        yield return new Customer { Id = "cust_s2", Email = "stream2@test.com", Status = CustomerStatus.Active };
        await Task.CompletedTask;
    }

    // Dictionary return type
    public Task<Dictionary<string, object>> GetMetadataAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(new Dictionary<string, object> { ["id"] = id, ["created"] = "2024-01-01" });

    // Non-async overload — collides with GetAsync after Async stripping
    public Customer Get(string id)
        => new() { Id = id, Email = "sync@example.com", Status = CustomerStatus.Active };
}
