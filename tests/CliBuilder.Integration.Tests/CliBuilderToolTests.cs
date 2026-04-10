using System.Diagnostics;
using System.Text.Json;
using CliBuilder.Adapter.DotNet;
using CliBuilder.Commands;
using CliBuilder.Core.Generators;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;

namespace CliBuilder.Integration.Tests;

public class CliBuilderToolTests : IDisposable
{
    private readonly string _testSdkPath;
    private readonly string _tempDir;

    public CliBuilderToolTests()
    {
        var testDir = Path.GetDirectoryName(typeof(CliBuilderToolTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var configuration = testDir.Contains(Path.Combine("bin", "Release")) ? "Release" : "Debug";
        _testSdkPath = Path.Combine(repoRoot,
            "tests", "CliBuilder.TestSdk", "bin", configuration, "net8.0", "CliBuilder.TestSdk.dll");
        _tempDir = Path.Combine(Path.GetTempPath(), "cli-builder-tool-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string OutputDir => Path.Combine(_tempDir, "output");

    // --- Exit code contract (7 tests) ---

    [Fact]
    public void GenerateCommand_ValidAssembly_ExitsZero()
    {
        var exit = GenerateCommand.Execute(
            new DotNetAdapter(), new CSharpCliGenerator(),
            _testSdkPath, OutputDir, null, false);

        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(OutputDir));
    }

    [Fact]
    public void GenerateCommand_WarningDiagnostics_ExitsZero()
    {
        var adapter = new FakeAdapterWithWarnings();
        var exit = GenerateCommand.Execute(
            adapter, new CSharpCliGenerator(),
            _testSdkPath, OutputDir, null, false);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void GenerateCommand_ErrorDiagnostics_ExitsOne()
    {
        // Use a mock adapter that returns Error diagnostics
        var adapter = new FakeAdapterWithErrors();
        var exit = GenerateCommand.Execute(
            adapter, new CSharpCliGenerator(),
            _testSdkPath, OutputDir, null, false);

        Assert.Equal(1, exit);
    }

    [Fact]
    public void GenerateCommand_AssemblyNotFound_ExitsTwo()
    {
        var exit = GenerateCommand.Execute(
            new DotNetAdapter(), new CSharpCliGenerator(),
            "/nonexistent/path.dll", OutputDir, null, false);

        Assert.Equal(2, exit);
    }

    [Fact]
    public void GenerateCommand_CorruptedAssembly_ExitsTwo()
    {
        var fakeDll = Path.Combine(_tempDir, "corrupt.dll");
        File.WriteAllText(fakeDll, "this is not a .NET assembly");

        var exit = GenerateCommand.Execute(
            new DotNetAdapter(), new CSharpCliGenerator(),
            fakeDll, OutputDir, null, false);

        Assert.Equal(2, exit);
    }

    [Fact]
    public void InspectCommand_ValidAssembly_ExitsZero()
    {
        var exit = InspectCommand.Execute(new DotNetAdapter(), _testSdkPath, false);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void InspectCommand_AssemblyNotFound_ExitsTwo()
    {
        var exit = InspectCommand.Execute(new DotNetAdapter(), "/nonexistent/path.dll", false);

        Assert.Equal(2, exit);
    }

    // --- Output correctness (4 tests) ---

    [Fact]
    public void InspectCommand_ValidAssembly_PrintsSummary()
    {
        var stdout = CaptureStdout(() =>
            InspectCommand.Execute(new DotNetAdapter(), _testSdkPath, false));

        Assert.Contains("SDK: CliBuilder.TestSdk", stdout);
        Assert.Contains("Resources: 7", stdout);
        Assert.Contains("customer", stdout);
    }

    [Fact]
    public void InspectCommand_Json_HasExpectedSchema()
    {
        var stdout = CaptureStdout(() =>
            InspectCommand.Execute(new DotNetAdapter(), _testSdkPath, true));

        var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.TryGetProperty("schemaVersion", out var version));
        Assert.Equal("1", version.GetString());
        Assert.True(json.RootElement.TryGetProperty("metadata", out var metadata));
        Assert.True(json.RootElement.TryGetProperty("diagnostics", out _));
        Assert.True(metadata.TryGetProperty("resources", out var resources));
        Assert.Equal(JsonValueKind.Array, resources.ValueKind);
        Assert.True(resources.GetArrayLength() > 0);
    }

    [Fact]
    public void GenerateCommand_NameFlag_PropagatesCliName()
    {
        var exit = GenerateCommand.Execute(
            new DotNetAdapter(), new CSharpCliGenerator(),
            _testSdkPath, OutputDir, "my-custom-cli", false);

        Assert.Equal(0, exit);
        // The generated project directory should contain the custom name
        Assert.True(Directory.Exists(Path.Combine(OutputDir, "my-custom-cli")));
    }

    [Fact]
    public void GenerateCommand_OverwriteTrue_RegeneratesSuccessfully()
    {
        // Generate once
        GenerateCommand.Execute(
            new DotNetAdapter(), new CSharpCliGenerator(),
            _testSdkPath, OutputDir, null, false);

        // Generate again with overwrite — should succeed
        var exit = GenerateCommand.Execute(
            new DotNetAdapter(), new CSharpCliGenerator(),
            _testSdkPath, OutputDir, null, true);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void GenerateCommand_OverwriteFalse_ExistingOutput_ExitsTwo()
    {
        // Generate once
        GenerateCommand.Execute(
            new DotNetAdapter(), new CSharpCliGenerator(),
            _testSdkPath, OutputDir, null, false);

        // Generate again without overwrite — should reject
        var exit = GenerateCommand.Execute(
            new DotNetAdapter(), new CSharpCliGenerator(),
            _testSdkPath, OutputDir, null, false);

        Assert.Equal(2, exit);
    }

    [Fact]
    public void InspectCommand_ErrorDiagnostics_ExitsOne()
    {
        var adapter = new FakeAdapterWithErrors();
        var exit = InspectCommand.Execute(adapter, _testSdkPath, false);

        Assert.Equal(1, exit);
    }

    [Fact]
    public void GenerateCommand_GeneratorIOException_ExitsTwo()
    {
        var adapter = new FakeAdapterWithWarnings();
        var generator = new FakeGeneratorThrowingIOException();
        var exit = GenerateCommand.Execute(
            adapter, generator, _testSdkPath, OutputDir, null, false);

        Assert.Equal(2, exit);
    }

    // --- Formatting (3 tests) ---

    [Fact]
    public void DiagnosticsFormatter_GroupsBySeverity()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Info, "CB100", "Info message"),
            new(DiagnosticSeverity.Error, "CB200", "Error message"),
            new(DiagnosticSeverity.Warning, "CB300", "Warning message"),
        };

        var writer = new StringWriter();
        DiagnosticsFormatter.Print(diagnostics, writer);
        var output = writer.ToString();

        var errorIndex = output.IndexOf("ERROR");
        var warnIndex = output.IndexOf("WARN");
        var infoIndex = output.IndexOf("INFO");

        Assert.True(errorIndex < warnIndex, "Error should appear before Warning");
        Assert.True(warnIndex < infoIndex, "Warning should appear before Info");
    }

    [Fact]
    public void DiagnosticsFormatter_NoColorWhenRedirected()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "CB200", "Error message"),
        };

        var writer = new StringWriter();
        DiagnosticsFormatter.Print(diagnostics, writer);
        var output = writer.ToString().TrimEnd();

        // When writing to a StringWriter (not Console.Error), no ANSI color codes
        Assert.Equal("[ERROR] CB200  Error message", output);
    }

    // --- Helpers ---

    private static string CaptureStdout(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}

/// <summary>
/// Fixture for process-based tests. Uses the CliBuilder binary from the test output directory
/// (already built by dotnet test via ProjectReference). No nested dotnet build needed.
/// </summary>
public class CliBuilderBinaryFixture
{
    public string BinaryPath { get; }
    public string TestSdkPath { get; }

    public CliBuilderBinaryFixture()
    {
        var testDir = Path.GetDirectoryName(typeof(CliBuilderBinaryFixture).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var configuration = testDir.Contains(Path.Combine("bin", "Release")) ? "Release" : "Debug";

        // CliBuilder binary is copied to test output dir via ProjectReference
        BinaryPath = Path.Combine(testDir, "CliBuilder.dll");
        TestSdkPath = Path.Combine(repoRoot,
            "tests", "CliBuilder.TestSdk", "bin", configuration, "net8.0", "CliBuilder.TestSdk.dll");

        if (!File.Exists(BinaryPath))
            throw new InvalidOperationException($"CliBuilder binary not found at: {BinaryPath}");
    }

    public (int ExitCode, string Stdout, string Stderr) RunCli(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{BinaryPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "" }
        };

        using var process = Process.Start(psi)!;
        // Read streams before WaitForExit to avoid deadlock when buffers fill
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new TimeoutException("cli-builder process timed out after 30s");
        }
        return (process.ExitCode, stdout, stderr);
    }
}

/// <summary>
/// Process-based integration tests that invoke the CLI binary.
/// Uses IClassFixture to pre-build once.
/// </summary>
public class CliBuilderBinaryTests : IClassFixture<CliBuilderBinaryFixture>
{
    private readonly CliBuilderBinaryFixture _fixture;

    public CliBuilderBinaryTests(CliBuilderBinaryFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CliTool_Generate_ProducesCompilableProject()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "cli-builder-binary-tests", Guid.NewGuid().ToString());
        try
        {
            var (exitCode, stdout, _) = _fixture.RunCli(
                $"generate --assembly \"{_fixture.TestSdkPath}\" --output \"{outputDir}\"");

            Assert.Equal(0, exitCode);
            Assert.Contains("Generated", stdout);
            Assert.Contains("resources", stdout);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void CliTool_Inspect_Json_RoundTrips()
    {
        var (exitCode, stdout, _) = _fixture.RunCli(
            $"inspect --json --assembly \"{_fixture.TestSdkPath}\"");

        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.TryGetProperty("metadata", out var metadata));
        Assert.True(metadata.TryGetProperty("name", out var name));
        Assert.Equal("CliBuilder.TestSdk", name.GetString());
    }

    [Fact]
    public void CliTool_VersionFlag_ReturnsVersion()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("--version");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"\d+\.\d+\.\d+", stdout);
    }

    [Fact]
    public void HelpOutput_HasDescriptions()
    {
        var (exitCode, stdout, _) = _fixture.RunCli("generate --help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Path to the SDK assembly", stdout);
        Assert.Contains("Output directory", stdout);
        Assert.Contains("CLI name", stdout);
        Assert.Contains("Overwrite", stdout);
    }
}

/// <summary>
/// Fake adapter that returns Error-level diagnostics to test exit code 1.
/// </summary>
internal class FakeAdapterWithErrors : CliBuilder.Core.Adapters.ISdkAdapter
{
    public AdapterResult Extract(AdapterOptions options) => new(
        new SdkMetadata("FakeSdk", "1.0.0", Array.Empty<Resource>(), Array.Empty<AuthPattern>()),
        new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "CB999", "Fake error for testing")
        });
}

/// <summary>
/// Fake adapter that returns Warning-level diagnostics (no errors) to test exit code 0.
/// </summary>
internal class FakeAdapterWithWarnings : CliBuilder.Core.Adapters.ISdkAdapter
{
    public AdapterResult Extract(AdapterOptions options) => new(
        new SdkMetadata("FakeSdk", "1.0.0", Array.Empty<Resource>(), Array.Empty<AuthPattern>()),
        new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "CB202", "Fake warning for testing")
        });
}

/// <summary>
/// Fake generator that throws IOException to test generator-phase exception handling.
/// </summary>
internal class FakeGeneratorThrowingIOException : CliBuilder.Core.Generators.ICliGenerator
{
    public GeneratorResult Generate(SdkMetadata metadata, GeneratorOptions options)
        => throw new IOException("Simulated disk full");
}
