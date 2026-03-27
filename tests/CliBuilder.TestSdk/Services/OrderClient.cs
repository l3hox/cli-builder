using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

public class OrderClient
{
    public OrderClient(string apiKey) { }

    public Task<ClientResult<Order>> CreateAsync(CreateOrderOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ClientResult<Order>> GetAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ClientResult<Order>> UpdateAsync(NestedOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ClientResult<Order>> ProcessAsync(SanitizationOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
