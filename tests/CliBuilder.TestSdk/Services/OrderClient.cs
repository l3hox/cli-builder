using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

public class OrderClient
{
    public OrderClient(string apiKey) { }

    public Task<ClientResult<Order>> CreateAsync(CreateOrderOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(new ClientResult<Order>
        {
            Value = new Order { Id = "ord_001", Amount = options.Amount }
        });

    public Task<ClientResult<Order>> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(new ClientResult<Order>
        {
            Value = new Order { Id = id, Amount = 99.99m }
        });

    public Task<ClientResult<Order>> UpdateAsync(NestedOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(new ClientResult<Order>
        {
            Value = new Order { Id = "ord_updated", Amount = 0m }
        });

    public Task<ClientResult<Order>> ProcessAsync(SanitizationOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(new ClientResult<Order>
        {
            Value = new Order { Id = "ord_processed", Amount = 0m }
        });
}
