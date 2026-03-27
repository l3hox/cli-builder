using System.Text.Json;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;

namespace CliBuilder.Generator.Tests;

public class CSharpCliGeneratorTests : IDisposable
{
    private readonly string _outputDir;
    private readonly SdkMetadata _testSdkMetadata;

    public CSharpCliGeneratorTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "cli-builder-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_outputDir);
        _testSdkMetadata = LoadTestSdkMetadata();
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    private static SdkMetadata LoadTestSdkMetadata()
    {
        var testDir = Path.GetDirectoryName(typeof(CSharpCliGeneratorTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var fixturePath = Path.Combine(repoRoot, "tests", "fixtures", "testsdk-metadata.json");
        var json = File.ReadAllText(fixturePath);
        var adapterResult = JsonSerializer.Deserialize<AdapterResult>(json, SdkMetadataJson.Options)!;
        return adapterResult.Metadata;
    }

    private GeneratorResult Generate(string? cliName = "testsdk-cli")
    {
        var generator = new CSharpCliGenerator();
        return generator.Generate(_testSdkMetadata, new GeneratorOptions(_outputDir, cliName));
    }

    // -----------------------------------------------------------
    // Project structure
    // -----------------------------------------------------------

    [Fact]
    public void Generate_CreatesProjectDirectory()
    {
        var result = Generate();
        Assert.True(Directory.Exists(result.ProjectDirectory));
    }

    [Fact]
    public void Generate_CreatesCsprojFile()
    {
        var result = Generate();
        var csproj = Path.Combine(result.ProjectDirectory, "testsdk-cli.csproj");
        Assert.True(File.Exists(csproj));
    }

    [Fact]
    public void Generate_CreatesProgramCs()
    {
        var result = Generate();
        var program = Path.Combine(result.ProjectDirectory, "Program.cs");
        Assert.True(File.Exists(program));
    }

    // -----------------------------------------------------------
    // .csproj content
    // -----------------------------------------------------------

    [Fact]
    public void Generate_CsprojReferencesSdkPackage()
    {
        var result = Generate();
        var csproj = File.ReadAllText(Path.Combine(result.ProjectDirectory, "testsdk-cli.csproj"));
        Assert.Contains("CliBuilder.TestSdk", csproj);
    }

    [Fact]
    public void Generate_CsprojReferencesSystemCommandLine()
    {
        var result = Generate();
        var csproj = File.ReadAllText(Path.Combine(result.ProjectDirectory, "testsdk-cli.csproj"));
        Assert.Contains("<PackageReference Include=\"System.CommandLine\"", csproj);
    }

    [Fact]
    public void Generate_CsprojTargetsNet8()
    {
        var result = Generate();
        var csproj = File.ReadAllText(Path.Combine(result.ProjectDirectory, "testsdk-cli.csproj"));
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", csproj);
    }

    [Fact]
    public void Generate_CsprojUsesForwardSlashes()
    {
        var result = Generate();
        var csproj = File.ReadAllText(Path.Combine(result.ProjectDirectory, "testsdk-cli.csproj"));
        Assert.DoesNotContain("\\", csproj);
    }

    // -----------------------------------------------------------
    // Program.cs content
    // -----------------------------------------------------------

    [Fact]
    public void Generate_ProgramRegistersAllResources()
    {
        var result = Generate();
        var program = File.ReadAllText(Path.Combine(result.ProjectDirectory, "Program.cs"));
        Assert.Contains("CustomerCommands", program);
        Assert.Contains("OrderCommands", program);
        Assert.Contains("ProductCommands", program);
    }

    [Fact]
    public void Generate_ProgramHasRootCommand()
    {
        var result = Generate();
        var program = File.ReadAllText(Path.Combine(result.ProjectDirectory, "Program.cs"));
        Assert.Contains("RootCommand", program);
    }

    // -----------------------------------------------------------
    // GeneratorResult
    // -----------------------------------------------------------

    [Fact]
    public void Generate_ReturnsAllGeneratedFiles()
    {
        var result = Generate();
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith(".csproj"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("Program.cs"));
        // All returned files must actually exist
        foreach (var file in result.GeneratedFiles)
            Assert.True(File.Exists(file), $"Generated file does not exist: {file}");
    }

    // -----------------------------------------------------------
    // CLI name handling
    // -----------------------------------------------------------

    [Fact]
    public void Generate_UsesCliNameOverride()
    {
        var result = Generate(cliName: "my-tool");
        Assert.EndsWith("my-tool", result.ProjectDirectory);
        var csproj = Path.Combine(result.ProjectDirectory, "my-tool.csproj");
        Assert.True(File.Exists(csproj));
    }

    [Fact]
    public void Generate_DefaultCliNameFromSdkName()
    {
        var result = Generate(cliName: null);
        var dirName = Path.GetFileName(result.ProjectDirectory)!;
        // "CliBuilder.TestSdk" → "clibuilder-testsdk"
        Assert.Equal("clibuilder-testsdk", dirName);
    }

    // -----------------------------------------------------------
    // Degenerate inputs
    // -----------------------------------------------------------

    [Fact]
    public void Generate_EmptyResources_ProducesMinimalProject()
    {
        var emptyMetadata = new SdkMetadata("EmptySdk", "1.0.0", new List<Resource>(), new List<AuthPattern>());
        var generator = new CSharpCliGenerator();
        var result = generator.Generate(emptyMetadata, new GeneratorOptions(_outputDir, "empty-cli"));

        Assert.True(File.Exists(Path.Combine(result.ProjectDirectory, "empty-cli.csproj")));
        Assert.True(File.Exists(Path.Combine(result.ProjectDirectory, "Program.cs")));
        // No Commands/ directory when there are no resources
        Assert.False(Directory.Exists(Path.Combine(result.ProjectDirectory, "Commands")));
    }

    [Fact]
    public void Generate_NullDescriptions_DoNotThrow()
    {
        // All descriptions in TestSdk fixture are null — this test ensures no NPE
        var result = Generate();
        Assert.NotEmpty(result.GeneratedFiles);
    }

    // -----------------------------------------------------------
    // Cross-platform: LF line endings
    // -----------------------------------------------------------

    [Fact]
    public void Generate_AllFilesUseLfLineEndings()
    {
        var result = Generate();
        foreach (var file in result.GeneratedFiles)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("\r\n", content);
        }
    }
}
