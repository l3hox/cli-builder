using System.Diagnostics;
using System.Text.Json;
using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;

namespace CliBuilder.Integration.Tests;

/// <summary>
/// End-to-end tests: generate CLI from TestSdk → build → run → assert output.
/// Uses IClassFixture to share the generate+build step across all tests.
/// </summary>
public class GeneratedCliFixture : IDisposable
{
    public string ProjectDir { get; }

    public GeneratedCliFixture()
    {
        var testDir = Path.GetDirectoryName(typeof(GeneratedCliFixture).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var outputDir = Path.Combine(Path.GetTempPath(), "cli-builder-e2e", Guid.NewGuid().ToString());

        // Extract metadata
        var adapter = new DotNetAdapter();
        var sdkPath = Path.Combine(repoRoot,
            "tests", "CliBuilder.TestSdk", "bin",
            testDir.Contains(Path.Combine("bin", "Release")) ? "Release" : "Debug",
            "net8.0", "CliBuilder.TestSdk.dll");
        var adapterResult = adapter.Extract(new AdapterOptions(sdkPath));

        // Generate with ProjectReference so it compiles against the real TestSdk
        var testSdkCsproj = Path.Combine(repoRoot, "tests", "CliBuilder.TestSdk", "CliBuilder.TestSdk.csproj");
        var generator = new CSharpCliGenerator();
        var result = generator.Generate(adapterResult.Metadata,
            new GeneratorOptions(outputDir, "testsdk-cli", SdkProjectPath: testSdkCsproj));
        ProjectDir = result.ProjectDirectory;

        // Build once — shared across all tests
        var buildResult = RunProcess("dotnet", $"build \"{ProjectDir}\" --verbosity quiet");
        if (buildResult.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet build failed (exit {buildResult.ExitCode}):\n{buildResult.Stdout}\n{buildResult.Stderr}");
    }

    public (int ExitCode, string Stdout, string Stderr) RunCli(string args)
    {
        return RunProcess("dotnet", $"run --project \"{ProjectDir}\" --no-build -- {args}");
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
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (process.ExitCode, stdout, stderr);
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
    }

    [Fact]
    public void CustomerStream_ReturnsMultipleItems()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("customer stream --json --api-key test-key");
        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.True(json.RootElement.GetArrayLength() >= 2);
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
}
