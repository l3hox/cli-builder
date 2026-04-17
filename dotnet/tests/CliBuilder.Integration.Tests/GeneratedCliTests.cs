using System.Diagnostics;
using System.Text.Json;
using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;
using CliBuilder.Tests;

namespace CliBuilder.Integration.Tests;

/// <summary>
/// End-to-end tests: generate CLI from TestSdk → build → run → assert output.
/// Uses IClassFixture to share the generate+build step across all tests.
/// </summary>
public class GeneratedCliFixture : IDisposable
{
    public string ProjectDir { get; }
    private string Configuration { get; }

    public GeneratedCliFixture()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "cli-builder-e2e", Guid.NewGuid().ToString());
        Configuration = TestPaths.Configuration;

        // Extract metadata
        var adapter = new DotNetAdapter();
        var adapterResult = adapter.Extract(new AdapterOptions(TestPaths.TestSdkAssembly));

        // Generate with ProjectReference so it compiles against the real TestSdk
        var testSdkCsproj = TestPaths.TestSdkCsproj;
        var generator = new CSharpCliGenerator();
        var result = generator.Generate(adapterResult.Metadata,
            new GeneratorOptions(outputDir, "testsdk-cli", SdkProjectPath: testSdkCsproj));
        ProjectDir = result.ProjectDirectory;

        // Build once — shared across all tests
        var buildResult = RunProcess("dotnet", $"build \"{ProjectDir}\" --configuration {Configuration} --verbosity quiet");
        if (buildResult.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet build failed (exit {buildResult.ExitCode}):\n{buildResult.Stdout}\n{buildResult.Stderr}");
    }

    public (int ExitCode, string Stdout, string Stderr) RunCli(string args)
    {
        return RunProcess("dotnet", $"run --project \"{ProjectDir}\" --configuration {Configuration} --no-build -- {args}");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "" }
        };

        using var process = Process.Start(psi)!;
        // Read async to prevent pipe buffer deadlock
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new TimeoutException($"Process '{fileName}' timed out after 30s");
        }
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(ProjectDir)))
            Directory.Delete(Path.GetDirectoryName(ProjectDir)!, recursive: true);
    }
}

public class GeneratedCliTests : IClassFixture<GeneratedCliFixture>
{
    private readonly GeneratedCliFixture _fixture;

    public GeneratedCliTests(GeneratedCliFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Help_ShowsCommands()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("--help");
        Assert.Equal(0, exitCode);
        Assert.Contains("customer", stdout);
        Assert.Contains("order", stdout);
        Assert.Contains("product", stdout);
    }

    [Fact]
    public void CustomerGet_ReturnsCustomerJson()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("customer get --id cust_123 --json --api-key test-key");
        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.Equal("cust_123", json.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void CustomerCreate_ReturnsCustomerWithEmail()
    {
        // --email and --preferred-contact are required
        var (exitCode, stdout, _) = _fixture.RunCli("customer create --email foo@bar.com --preferred-contact true --json --api-key test-key");
        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.Equal("foo@bar.com", json.RootElement.GetProperty("email").GetString());
        Assert.Equal("cust_001", json.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void CustomerList_ReturnsArray()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("customer list --json --api-key test-key");
        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(2, json.RootElement.GetArrayLength());
    }

    [Fact]
    public void CustomerDelete_Succeeds()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("customer delete --id cust_001 --json --api-key test-key");
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout), "delete should produce output");
    }

    [Fact]
    public void CustomerStream_ReturnsMultipleItems()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("customer stream --json --api-key test-key");
        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(2, json.RootElement.GetArrayLength());
    }

    [Fact]
    public void ProductList_TokenCredentialAuth_Works()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("product list --json --api-key test-key");
        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.Equal("prod_001", json.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void NoCredential_ExitsWithCode2()
    {
        // Clear env var to ensure no credential is found
        var (exitCode, _, stderr) = _fixture.RunCli("customer get --id x");
        Assert.Equal(2, exitCode);
        Assert.Contains("auth_error", stderr);
    }

    [Fact]
    public void CustomerGetMetadata_ReturnsDictionary()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("customer get-metadata --id meta_123 --json --api-key test-key");
        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.Equal("meta_123", json.RootElement.GetProperty("id").GetString());
    }

    // -----------------------------------------------------------
    // Council review additions
    // -----------------------------------------------------------

    [Fact]
    public void OrderGet_ReturnsOrderJson()
    {
        // OrderClient returns ClientResult<Order> — serialized with the wrapper.
        // The JSON contains "value" with the inner Order.
        var (exitCode, stdout, _) = _fixture.RunCli("order get --id ord_123 --json --api-key test-key");
        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.TryGetProperty("value", out var value),
            "Expected ClientResult wrapper with 'value' property");
        Assert.Equal("ord_123", value.GetProperty("id").GetString());
    }

    [Fact]
    public void MissingRequiredParam_ExitsNonZero()
    {
        // customer get requires --id (a direct method param) — omitting it should fail
        var (exitCode, _, stderr) = _fixture.RunCli("customer get --api-key test-key");
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void UnknownCommand_ExitsNonZero()
    {
        var (exitCode, _, _) = _fixture.RunCli("nonexistent-command");
        Assert.NotEqual(0, exitCode);
    }

    // -----------------------------------------------------------
    // --json-input tests (step 9)
    // -----------------------------------------------------------

    [Fact]
    public void OrderUpdate_WithJsonInput_PopulatesNestedObject()
    {
        var jsonInput = @"{""name"":""Updated"",""shippingAddress"":{""line1"":""123 Main"",""city"":""Springfield"",""country"":""US""}}";
        var (exitCode, stdout, stderr) = _fixture.RunCli($@"order update --json-input ""{jsonInput.Replace("\"", "\\\"")}"" --json --api-key test-key");
        Assert.True(exitCode == 0, $"Exit {exitCode}, stderr: {stderr}");
        var json = JsonDocument.Parse(stdout);
        Assert.Equal("Updated", json.RootElement.GetProperty("value").GetProperty("name").GetString());
    }

    [Fact]
    public void OrderUpdate_FlatFlagOverridesJsonInput()
    {
        var jsonInput = @"{""name"":""FromJson""}";
        var (exitCode, stdout, stderr) = _fixture.RunCli($@"order update --json-input ""{jsonInput.Replace("\"", "\\\"")}"" --name Override --json --api-key test-key");
        Assert.True(exitCode == 0, $"Exit {exitCode}, stderr: {stderr}");
        var json = JsonDocument.Parse(stdout);
        Assert.Equal("Override", json.RootElement.GetProperty("value").GetProperty("name").GetString());
    }

    [Fact]
    public void InvalidJsonInput_ExitsWithCode1()
    {
        var (exitCode, _, stderr) = _fixture.RunCli("order update --json-input not-valid-json --api-key test-key");
        Assert.Equal(1, exitCode);
        Assert.Contains("json_input_error", stderr);
    }

    // --- Direct param deserialization tests (Step 9B) ---

    [Fact]
    public void Help_ShowsMessageClientCommands()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("--help");
        Assert.Equal(0, exitCode);
        Assert.Contains("message", stdout);
    }

    private static string JsonArg(string json) =>
        $@"""{json.Replace("\"", "\\\"")}""";

    [Fact]
    public void MessageBatch_WithJsonInput_DeserializesStringList()
    {
        var ji = JsonArg(@"{""ids"":[""a"",""b"",""c""]}");
        var (exitCode, stdout, stderr) = _fixture.RunCli(
            $"message batch --json-input {ji} --json --api-key test-key");
        Assert.True(exitCode == 0, $"Exit {exitCode}, stderr: {stderr}");
        var json = JsonDocument.Parse(stdout);
        var value = json.RootElement.GetProperty("value");
        Assert.Equal("batch_001", value.GetProperty("id").GetString());
        Assert.Contains("a,b,c", value.GetProperty("name").GetString());
    }

    [Fact]
    public void MessageBatch_EmptyArray_Succeeds()
    {
        var ji = JsonArg(@"{""ids"":[]}");
        var (exitCode, stdout, stderr) = _fixture.RunCli(
            $"message batch --json-input {ji} --json --api-key test-key");
        Assert.True(exitCode == 0, $"Exit {exitCode}, stderr: {stderr}");
        var json = JsonDocument.Parse(stdout);
        Assert.Equal("batch_001", json.RootElement.GetProperty("value").GetProperty("id").GetString());
    }

    [Fact]
    public void MessageSend_WithJsonInput_DeserializesMessageList()
    {
        var ji = JsonArg(@"{""messages"":[{""$type"":""user"",""content"":""hello""}]}");
        var (exitCode, stdout, stderr) = _fixture.RunCli(
            $"message send --json-input {ji} --json --api-key test-key");
        Assert.True(exitCode == 0, $"Exit {exitCode}, stderr: {stderr}");
        var json = JsonDocument.Parse(stdout);
        Assert.Contains("Sent 1 messages", json.RootElement.GetProperty("value").GetProperty("name").GetString());
    }

    [Fact]
    public void MessageSend_MixedJsonInput_PopulatesBothParamsAndOptions()
    {
        var ji = JsonArg(@"{""messages"":[{""$type"":""user"",""content"":""hi""}],""model"":""gpt-4""}");
        var (exitCode, stdout, stderr) = _fixture.RunCli(
            $"message send --json-input {ji} --json --api-key test-key");
        Assert.True(exitCode == 0, $"Exit {exitCode}, stderr: {stderr}");
        var json = JsonDocument.Parse(stdout);
        var name = json.RootElement.GetProperty("value").GetProperty("name").GetString()!;
        Assert.Contains("Sent 1 messages", name);
        Assert.Contains("with model gpt-4", name);
    }

    [Fact]
    public void MessageSend_MissingRequiredDirectParam_ExitsWithError()
    {
        // No --json-input at all, but messages is required
        var (exitCode, _, stderr) = _fixture.RunCli(
            "message send --json --api-key test-key");
        Assert.Equal(1, exitCode);
        Assert.Contains("missing_required_param", stderr);
    }

    [Fact]
    public void MessageSend_JsonInputMissingKey_ExitsWithError()
    {
        // --json-input provided but no "messages" key
        var ji = JsonArg(@"{""model"":""gpt-4""}");
        var (exitCode, _, stderr) = _fixture.RunCli(
            $"message send --json-input {ji} --json --api-key test-key");
        Assert.Equal(1, exitCode);
        Assert.Contains("missing_required_param", stderr);
    }

    [Fact]
    public void MessageBatch_TypeMismatch_ExitsWithJsonError()
    {
        var ji = JsonArg(@"{""ids"":""not-an-array""}");
        var (exitCode, _, stderr) = _fixture.RunCli(
            $"message batch --json-input {ji} --json --api-key test-key");
        Assert.Equal(1, exitCode);
        Assert.Contains("json_input_error", stderr);
    }

    [Fact]
    public void MessageSend_NullJsonValue_ExitsWithError()
    {
        // "messages" key present but value is null → deserialization returns null → required guard fires
        var ji = JsonArg(@"{""messages"":null}");
        var (exitCode, _, stderr) = _fixture.RunCli(
            $"message send --json-input {ji} --json --api-key test-key");
        Assert.Equal(1, exitCode);
        Assert.Contains("missing_required_param", stderr);
    }

    [Fact]
    public void MessageSend_FlatFlagOverridesOptions_NotDirectParam()
    {
        // --json-input provides messages (direct param) + model (options)
        // --model flat flag should override the JSON model value
        var ji = JsonArg(@"{""messages"":[{""$type"":""user"",""content"":""hi""}],""model"":""from-json""}");
        var (exitCode, stdout, stderr) = _fixture.RunCli(
            $"message send --json-input {ji} --model override --json --api-key test-key");
        Assert.True(exitCode == 0, $"Exit {exitCode}, stderr: {stderr}");
        var json = JsonDocument.Parse(stdout);
        var name = json.RootElement.GetProperty("value").GetProperty("name").GetString()!;
        Assert.Contains("with model override", name);
    }
}
