using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

public class ProductApi
{
    public ProductApi(TokenCredential credential) { }

    public Task<Product> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new Product { Id = "prod_001", Name = "Widget" });
}
