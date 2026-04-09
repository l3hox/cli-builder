using CliBuilder.TestSdk.Models;

namespace CliBuilder.TestSdk.Services;

public class MessageClient
{
    public MessageClient(string apiKey) { }

    /// <summary>
    /// IEnumerable&lt;AbstractType&gt; + options class → both via --json-input
    /// </summary>
    public Task<ClientResult<Order>> SendAsync(
        IEnumerable<Message> messages,
        SendMessageOptions? options = null,
        CancellationToken ct = default)
        => Task.FromResult(new ClientResult<Order>
        {
            Value = new Order
            {
                Id = "msg_001",
                Name = $"Sent {messages.Count()} messages"
                    + (options?.Model != null ? $" with model {options.Model}" : "")
            }
        });

    /// <summary>
    /// IEnumerable&lt;string&gt; direct param → simple concrete case
    /// </summary>
    public Task<ClientResult<Order>> BatchAsync(
        IEnumerable<string> ids,
        CancellationToken ct = default)
        => Task.FromResult(new ClientResult<Order>
        {
            Value = new Order { Id = "batch_001", Name = string.Join(",", ids) }
        });
}
