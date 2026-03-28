using System.Diagnostics;
using System.Text.Json;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;

namespace CliBuilder.Integration.Tests;

public class OpenAiGeneratorTests : IDisposable
{
    private readonly string _outputDir;
    private readonly SdkMetadata _openAiMetadata;

    public OpenAiGeneratorTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "cli-builder-openai-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_outputDir);
        _openAiMetadata = LoadOpenAiMetadata();
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
            var testDir = Path.GetDirectoryName(typeof(OpenAiGeneratorTests).Assembly.Location)!;
            return Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        }
    }

    private static SdkMetadata LoadOpenAiMetadata()
    {
        var fixturePath = Path.Combine(RepoRoot, "tests", "fixtures", "openai-metadata.json");
        var json = File.ReadAllText(fixturePath);
        var adapterResult = JsonSerializer.Deserialize<AdapterResult>(json, SdkMetadataJson.Options)!;
        return adapterResult.Metadata;
    }

    private GeneratorResult Generate()
    {
        var generator = new CSharpCliGenerator();
        return generator.Generate(_openAiMetadata, new GeneratorOptions(_outputDir, "openai-cli"));
    }

    [Fact]
    public void GenerateOpenAi_ProducesExpectedFileCount()
    {
        var result = Generate();
        // 20 resources → 20 command files + csproj + Program.cs + 2 formatters + auth = 25
        Assert.Equal(25, result.GeneratedFiles.Count);
    }

    [Fact]
    public void GenerateOpenAi_AllFilesUseLfLineEndings()
    {
        var result = Generate();
        foreach (var file in result.GeneratedFiles)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("\r\n", content);
        }
    }

    [Fact]
    public async Task GenerateOpenAi_Compiles()
    {
        var result = Generate();

        // OpenAI is a real NuGet package — PackageReference should resolve
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
                $"OpenAI CLI dotnet build failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
        }
    }

    [Fact]
    public void GenerateOpenAi_NoDiagnosticErrors()
    {
        var result = Generate();
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }
}
