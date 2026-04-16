using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

// Noun collision pair: ShippingService and ShippingClient both map to "shipping"
// Neither should appear in resources; adapter emits CB202 error.
public class ShippingService
{
    public Task<Order> TrackAsync(string trackingId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

public class ShippingClient
{
    public Task<Order> TrackAsync(string trackingId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
