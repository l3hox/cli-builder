namespace CliBuilder.TestSdk.Models;

// Simulates ApiKeyCredential pattern (like OpenAI SDK)
public class TokenCredential
{
    public TokenCredential(string token) { }
}

// Simulates ClientResult<T> pattern (like OpenAI SDK)
public class ClientResult<T>
{
    public T Value { get; set; } = default!;
}

// Simulates RequestOptions pattern (used in overload testing)
public class RequestOptions
{
    public string? IdempotencyKey { get; set; }
    public TimeSpan? Timeout { get; set; }
}
