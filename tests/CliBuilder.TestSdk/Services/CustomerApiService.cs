using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

public class CustomerApiService
{
    public Task<Customer> SearchAsync(string query, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
