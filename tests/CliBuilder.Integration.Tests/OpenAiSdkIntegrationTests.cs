using System.Text.Json;
using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;

namespace CliBuilder.Integration.Tests;

public class OpenAiSdkIntegrationTests
{
    private static readonly string OpenAiAssemblyPath = GetOpenAiAssemblyPath();
    private static readonly string FixturesDir = GetFixturesDir();

    private static string GetOpenAiAssemblyPath()
    {
        // OpenAI is a PackageReference — its DLL lands in the test output directory
        var testDir = Path.GetDirectoryName(typeof(OpenAiSdkIntegrationTests).Assembly.Location)!;
        var sdkPath = Path.Combine(testDir, "OpenAI.dll");

        if (!File.Exists(sdkPath))
            throw new InvalidOperationException(
                $"OpenAI.dll not found at: {sdkPath}. Ensure the project has the OpenAI NuGet package.");

        return sdkPath;
    }

    private static string GetFixturesDir()
    {
        var testDir = Path.GetDirectoryName(typeof(OpenAiSdkIntegrationTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "tests", "fixtures");
    }

    private AdapterResult ExtractOpenAi()
    {
        var adapter = new DotNetAdapter();
        return adapter.Extract(new AdapterOptions(OpenAiAssemblyPath));
    }

    // -------------------------------------------------------
    // Structural validation
    // -------------------------------------------------------

    [Fact]
    public void ExtractOpenAi_ProducesResources()
    {
        var result = ExtractOpenAi();
        Assert.NotEmpty(result.Metadata.Resources);
        Assert.Equal("OpenAI", result.Metadata.Name);
    }

    [Fact]
    public void ExtractOpenAi_DiscoversExpectedClients()
    {
        var result = ExtractOpenAi();
        var names = result.Metadata.Resources.Select(r => r.Name).ToList();

        // Core clients — these must be present
        Assert.Contains("chat", names);
        Assert.Contains("embedding", names);
        Assert.Contains("image", names);
        Assert.Contains("audio", names);
    }

    [Fact]
    public void ExtractOpenAi_NoNounCollisions()
    {
        var result = ExtractOpenAi();
        var nounCollisions = result.Diagnostics.Where(d => d.Code == "CB202").ToList();
        // Noun collisions (two classes → same resource name) should not happen in OpenAI SDK
        Assert.Empty(nounCollisions);
    }

    [Fact]
    public void ExtractOpenAi_NoGenuineVerbCollisions()
    {
        var result = ExtractOpenAi();
        // CB201 verb collisions — sync+async pairs are handled silently now.
        // Only genuine collisions (different method bases → same verb) should remain.
        var verbCollisions = result.Diagnostics.Where(d => d.Code == "CB201").ToList();
        // Log any remaining for inspection
        foreach (var d in verbCollisions)
            Console.WriteLine($"  CB201: {d.Message}");
        Assert.Empty(verbCollisions);
    }

    [Fact]
    public void ExtractOpenAi_DetectsAuthPattern()
    {
        var result = ExtractOpenAi();
        Assert.NotEmpty(result.Metadata.AuthPatterns);
        // OpenAI SDK uses ApiKeyCredential — should be detected as ApiKey
        Assert.Contains(result.Metadata.AuthPatterns,
            a => a.Type == AuthType.ApiKey || a.Type == AuthType.BearerToken);
    }

    [Fact]
    public void ExtractOpenAi_AsyncWrappersAreUnwrapped()
    {
        var result = ExtractOpenAi();
        foreach (var resource in result.Metadata.Resources)
        {
            foreach (var op in resource.Operations)
            {
                // Async wrappers must never appear as return types
                Assert.NotEqual("Task", op.ReturnType.Name);
                Assert.NotEqual("ValueTask", op.ReturnType.Name);
                // Generic ClientResult<T> must be unwrapped to T
                // Non-generic ClientResult (base class) is a legitimate return type
                if (op.ReturnType.Kind == TypeKind.Generic)
                {
                    Assert.NotEqual("ClientResult", op.ReturnType.Name);
                    Assert.NotEqual("AsyncCollectionResult", op.ReturnType.Name);
                    Assert.NotEqual("CollectionResult", op.ReturnType.Name);
                }
            }
        }
    }

    [Fact]
    public void ExtractOpenAi_HasStreamingOperations()
    {
        var result = ExtractOpenAi();
        var streamingOps = result.Metadata.Resources
            .SelectMany(r => r.Operations)
            .Where(o => o.IsStreaming)
            .ToList();
        Assert.NotEmpty(streamingOps);
    }

    // -------------------------------------------------------
    // Fixture output
    // -------------------------------------------------------

    [Fact]
    public void ExtractOpenAi_WritesFixtureAndSummary()
    {
        var result = ExtractOpenAi();

        // Write JSON fixture
        var json = JsonSerializer.Serialize(result, SdkMetadataJson.Options);
        Directory.CreateDirectory(FixturesDir);
        var fixturePath = Path.Combine(FixturesDir, "openai-metadata.json");
        File.WriteAllText(fixturePath, json);
        Assert.True(File.Exists(fixturePath));

        // Log summary
        Console.WriteLine($"OpenAI SDK: {result.Metadata.Name} v{result.Metadata.Version}");
        Console.WriteLine($"Resources: {result.Metadata.Resources.Count}");
        foreach (var resource in result.Metadata.Resources)
        {
            var ops = string.Join(", ", resource.Operations.Select(o =>
                $"{o.Name}({o.Parameters.Count}p)" + (o.IsStreaming ? " [stream]" : "")));
            Console.WriteLine($"  {resource.Name}: {ops}");
        }

        Console.WriteLine($"\nDiagnostics ({result.Diagnostics.Count}):");
        foreach (var d in result.Diagnostics)
        {
            Console.WriteLine($"  [{d.Severity}] {d.Code}: {d.Message}");
        }

        Console.WriteLine($"\nAuth ({result.Metadata.AuthPatterns.Count}):");
        foreach (var a in result.Metadata.AuthPatterns)
        {
            Console.WriteLine($"  {a.Type}: {a.ParameterName} → {a.EnvVar}");
        }
    }
}
