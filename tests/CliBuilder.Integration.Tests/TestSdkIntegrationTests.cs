using System.Text.Json;
using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;

namespace CliBuilder.Integration.Tests;

public class TestSdkIntegrationTests
{
    private static readonly string TestSdkAssemblyPath = GetTestSdkAssemblyPath();
    private static readonly string FixturesDir = GetFixturesDir();

    private static string GetTestSdkAssemblyPath()
    {
        var testDir = Path.GetDirectoryName(typeof(TestSdkIntegrationTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var configuration = testDir.Contains(Path.Combine("bin", "Release")) ? "Release" : "Debug";
        var sdkPath = Path.Combine(repoRoot,
            "tests", "CliBuilder.TestSdk", "bin", configuration, "net8.0", "CliBuilder.TestSdk.dll");

        if (!File.Exists(sdkPath))
            throw new InvalidOperationException(
                $"TestSdk assembly not found at: {sdkPath}. Ensure the solution is built before running tests.");

        return sdkPath;
    }

    private static string GetFixturesDir()
    {
        var testDir = Path.GetDirectoryName(typeof(TestSdkIntegrationTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "tests", "fixtures");
    }

    [Fact]
    public void ExtractTestSdk_ProducesValidMetadata_AndWritesFixture()
    {
        var adapter = new DotNetAdapter();
        var result = adapter.Extract(new AdapterOptions(TestSdkAssemblyPath));

        // Basic structural assertions
        Assert.NotEmpty(result.Metadata.Resources);
        Assert.NotEmpty(result.Metadata.AuthPatterns);
        Assert.Equal("CliBuilder.TestSdk", result.Metadata.Name);

        // Write full result (metadata + diagnostics) as JSON fixture
        var json = JsonSerializer.Serialize(result, SdkMetadataJson.Options);
        var fixturePath = Path.Combine(FixturesDir, "testsdk-metadata.json");
        Directory.CreateDirectory(FixturesDir);
        File.WriteAllText(fixturePath, json);

        // Verify the file was written and is valid JSON
        Assert.True(File.Exists(fixturePath));
        var parsed = JsonSerializer.Deserialize<AdapterResult>(File.ReadAllText(fixturePath), SdkMetadataJson.Options);
        Assert.NotNull(parsed);
        Assert.Equal(result.Metadata.Resources.Count, parsed.Metadata.Resources.Count);
    }

    [Fact]
    public void ExtractTestSdk_ResourceSummary()
    {
        var adapter = new DotNetAdapter();
        var result = adapter.Extract(new AdapterOptions(TestSdkAssemblyPath));

        // Log a human-readable summary to test output
        foreach (var resource in result.Metadata.Resources)
        {
            var ops = string.Join(", ", resource.Operations.Select(o =>
                $"{o.Name}({o.Parameters.Count} params)" + (o.IsStreaming ? " [streaming]" : "")));
            // xUnit captures this via ITestOutputHelper, but Console.WriteLine works in verbose mode
            Console.WriteLine($"  {resource.Name}: {ops}");
        }

        Console.WriteLine($"\nDiagnostics ({result.Diagnostics.Count}):");
        foreach (var d in result.Diagnostics)
        {
            Console.WriteLine($"  [{d.Severity}] {d.Code}: {d.Message}");
        }

        Console.WriteLine($"\nAuth patterns ({result.Metadata.AuthPatterns.Count}):");
        foreach (var a in result.Metadata.AuthPatterns)
        {
            Console.WriteLine($"  {a.Type}: {a.ParameterName} → {a.EnvVar}");
        }
    }
}
