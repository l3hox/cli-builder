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
