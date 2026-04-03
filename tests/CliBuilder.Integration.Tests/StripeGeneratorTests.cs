using System.Diagnostics;
using System.Text.Json;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;

namespace CliBuilder.Integration.Tests;

public class StripeGeneratorTests : IDisposable
{
    private readonly string _outputDir;
    private readonly SdkMetadata _stripeMetadata;

    public StripeGeneratorTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "cli-builder-stripe-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_outputDir);
        _stripeMetadata = LoadStripeMetadata();
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    private static string RepoRoot
    {
        get
        {
            var testDir = Path.GetDirectoryName(typeof(StripeGeneratorTests).Assembly.Location)!;
            return Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        }
    }

    private static SdkMetadata LoadStripeMetadata()
    {
        var fixturePath = Path.Combine(RepoRoot, "tests", "fixtures", "stripe-metadata.json");
        if (!File.Exists(fixturePath))
            throw new InvalidOperationException(
                $"Stripe fixture not found at: {fixturePath}. Run StripeSdkIntegrationTests.ExtractStripe_WritesFixture first.");
        var json = File.ReadAllText(fixturePath);
        var adapterResult = JsonSerializer.Deserialize<AdapterResult>(json, SdkMetadataJson.Options)!;
        return adapterResult.Metadata;
    }

    private GeneratorResult Generate()
    {
        var generator = new CSharpCliGenerator();
        return generator.Generate(_stripeMetadata, new GeneratorOptions(_outputDir, "stripe-cli"));
    }

    [Fact]
    public void GenerateStripe_ProducesFiles()
    {
        var result = Generate();
        // Stripe has 100+ resources → 100+ command files + boilerplate
        Assert.True(result.GeneratedFiles.Count > 50,
            $"Expected 50+ files, got {result.GeneratedFiles.Count}");
    }

    [Fact]
    public void GenerateStripe_AllFilesUseLfLineEndings()
    {
        var result = Generate();
        foreach (var file in result.GeneratedFiles)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("\r\n", content);
        }
    }

    [Fact]
    public void GenerateStripe_NoDiagnosticErrors()
    {
        var result = Generate();
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public async Task GenerateStripe_Compiles()
    {
        var result = Generate();

        var psi = new ProcessStartInfo("dotnet", $"build \"{result.ProjectDirectory}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Assert.Fail($"Could not start 'dotnet build' — is dotnet on PATH? {ex.Message}");
            return;
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(process.ExitCode == 0,
                $"Stripe CLI dotnet build failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
        }
    }
}
