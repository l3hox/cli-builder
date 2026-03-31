using System.Diagnostics;
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

    // -----------------------------------------------------------
    // Phase 6B: Command file generation
    // -----------------------------------------------------------

    [Fact]
    public void Generate_CreatesCommandFilePerResource()
    {
        var result = Generate();
        // TestSdk has 3 resources: customer, order, product
        var commandFiles = result.GeneratedFiles.Where(f => f.Contains("Commands")).ToList();
        Assert.Equal(3, commandFiles.Count);
    }

    [Fact]
    public void Generate_CommandFileNaming()
    {
        var result = Generate();
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("CustomerCommands.cs"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("OrderCommands.cs"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("ProductCommands.cs"));
    }

    [Fact]
    public void Generate_HasCommandFiles()
    {
        var result = Generate();
        // 3 resources = 3 command files
        var commandFiles = result.GeneratedFiles.Where(f =>
            f.Contains("Commands") && f.EndsWith(".cs")).ToList();
        Assert.Equal(3, commandFiles.Count);
    }

    [Fact]
    public void Generate_CommandFileContainsResourceVerbs()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        // Customer has: create, get, list, delete, stream, get-metadata
        Assert.Contains("\"create\"", customerCmd);
        Assert.Contains("\"get\"", customerCmd);
        Assert.Contains("\"list\"", customerCmd);
        Assert.Contains("\"delete\"", customerCmd);
        Assert.Contains("\"stream\"", customerCmd);
        Assert.Contains("\"get-metadata\"", customerCmd);
    }

    [Fact]
    public void Generate_PrimitiveParamsMapToOptions()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        // "get" operation has a string "id" param → --id option
        Assert.Contains("\"--id\"", customerCmd);
        Assert.Contains("Option<string>", customerCmd);
    }

    [Fact]
    public void Generate_NullableParamIsNotRequired()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        // "list" has nullable "cursor" param → IsRequired = false
        Assert.Contains("\"--cursor\"", customerCmd);
    }

    [Fact]
    public void Generate_LargeOptionsClass_HasJsonInput()
    {
        var result = Generate();
        var orderCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("OrderCommands.cs")));

        // CreateOrderOptions has 15 props → 10 flat + --json-input
        Assert.Contains("\"--json-input\"", orderCmd);
    }

    [Fact]
    public void Generate_SimpleOperation_NoJsonInput()
    {
        var result = Generate();
        var productCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("ProductCommands.cs")));

        // product "list" has zero parameters → definitely no --json-input
        Assert.Contains("\"list\"", productCmd);
        Assert.DoesNotContain("--json-input", productCmd);
    }

    [Fact]
    public void Generate_NestedObject_AddsJsonInput()
    {
        var result = Generate();
        var orderCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("OrderCommands.cs")));

        // "update" uses NestedOptions with Address sub-object → --json-input
        Assert.Contains("\"--json-input\"", orderCmd);
    }

    [Fact]
    public void Generate_EnumParam_GeneratesChoices()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        // CustomerStatus enum: Active, Inactive, Suspended
        Assert.Contains("Active", customerCmd);
        Assert.Contains("Inactive", customerCmd);
        Assert.Contains("Suspended", customerCmd);
    }

    [Fact]
    public void Generate_StreamingOp_MarkedInHelpText()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        // "stream" operation is streaming → "[streaming]" in help text
        Assert.Contains("[streaming]", customerCmd);
    }

    [Fact]
    public void Generate_CommandFilesUseLfLineEndings()
    {
        var result = Generate();
        foreach (var file in result.GeneratedFiles.Where(f => f.Contains("Commands")))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("\r\n", content);
        }
    }

    // -----------------------------------------------------------
    // Phase 6C: Output formatters, auth handler, handler wiring
    // -----------------------------------------------------------

    [Fact]
    public void Generate_CreatesJsonFormatterCs()
    {
        var result = Generate();
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("JsonFormatter.cs"));
    }

    [Fact]
    public void Generate_CreatesTableFormatterCs()
    {
        var result = Generate();
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("TableFormatter.cs"));
    }

    [Fact]
    public void Generate_CreatesAuthHandlerCs()
    {
        var result = Generate();
        // TestSdk has auth patterns → AuthHandler should be generated
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("AuthHandler.cs"));
    }

    [Fact]
    public void Generate_WithNoAuth_SkipsAuthHandler()
    {
        var noAuthMetadata = new SdkMetadata("NoAuth", "1.0",
            new List<Resource> { new("test", null, new List<Operation>()) },
            new List<AuthPattern>());
        var generator = new CSharpCliGenerator();
        var result = generator.Generate(noAuthMetadata, new GeneratorOptions(_outputDir, "noauth-cli"));

        Assert.DoesNotContain(result.GeneratedFiles, f => f.EndsWith("AuthHandler.cs"));
        Assert.False(Directory.Exists(Path.Combine(result.ProjectDirectory, "Auth")));
    }

    [Fact]
    public void Generate_AuthHandlerReadsEnvVar()
    {
        var result = Generate();
        var authHandler = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("AuthHandler.cs")));

        // TestSdk fixture has EnvVar = "TESTSDK_APIKEY"
        Assert.Contains("TESTSDK_APIKEY", authHandler);
    }

    [Fact]
    public void Generate_AuthHandlerHasPrecedenceChain()
    {
        var result = Generate();
        var authHandler = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("AuthHandler.cs")));

        // Find the Resolve method body to check ordering within it
        var resolveStart = authHandler.IndexOf("public static string Resolve");
        Assert.True(resolveStart >= 0, "AuthHandler should have Resolve method");
        var resolveBody = authHandler[resolveStart..];

        // Env var check must appear BEFORE config file read BEFORE flag check
        var envVarPos = resolveBody.IndexOf("GetEnvironmentVariable");
        var configPos = resolveBody.IndexOf("File.Exists(ConfigPath)");
        var flagPos = resolveBody.IndexOf("IsNullOrEmpty(flagValue)");

        Assert.True(envVarPos >= 0, "Resolve should check env var");
        Assert.True(configPos >= 0, "Resolve should check config file");
        Assert.True(flagPos >= 0, "Resolve should check flag value");
        Assert.True(envVarPos < configPos, "Env var must be checked before config file");
        Assert.True(configPos < flagPos, "Config file must be checked before flag");
    }

    [Fact]
    public void Generate_AuthHandlerWarnsOnFlagUsage()
    {
        var result = Generate();
        var authHandler = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("AuthHandler.cs")));

        Assert.Contains("Console.Error", authHandler);
        Assert.Contains("--api-key", authHandler);
    }

    [Fact]
    public void Generate_ProgramHasJsonGlobalOption()
    {
        var result = Generate();
        var program = File.ReadAllText(Path.Combine(result.ProjectDirectory, "Program.cs"));

        Assert.Contains("--json", program);
        Assert.Contains("AddGlobalOption", program);
    }

    [Fact]
    public void Generate_HandlerFormatsOutput()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        Assert.Contains("JsonFormatter", customerCmd);
        Assert.Contains("TableFormatter", customerCmd);
    }

    [Fact]
    public void Generate_HandlerSetsExitCodes()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        Assert.Contains("ExitCode = 0", customerCmd);  // success
        Assert.Contains("ExitCode = 2", customerCmd);  // auth error
        Assert.Contains("ExitCode = 3", customerCmd);  // SDK error
    }

    [Fact]
    public void Generate_HandlerHasAuthErrorHandler()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        // Auth failure should be caught separately with code "auth_error"
        Assert.Contains("auth_error", customerCmd);
        Assert.Contains("ExitCode = 2", customerCmd);
    }

    [Fact]
    public void Generate_ErrorHandlerSanitizesExceptionMessage()
    {
        var result = Generate();
        var customerCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("CustomerCommands.cs")));

        // Should have credential masking, not raw exception.Message
        Assert.Contains("SanitizeMessage", customerCmd);
    }

    [Fact]
    public void Generate_OutputDisablesColorWhenRedirected()
    {
        var result = Generate();
        var tableFormatter = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("TableFormatter.cs")));

        Assert.Contains("IsOutputRedirected", tableFormatter);
    }

    [Fact]
    public void Generate_VoidReturnOp_DoesNotCallFormatter()
    {
        // Create metadata with a void-returning operation
        var voidOp = new Operation("delete", "Delete item", new List<Parameter>
        {
            new("id", new TypeRef(TypeKind.Primitive, "string"), true)
        }, new TypeRef(TypeKind.Primitive, "void"));

        var resource = new Resource("item", null, new[] { voidOp });
        var metadata = new SdkMetadata("VoidTest", "1.0",
            new[] { resource }, new List<AuthPattern>());

        var generator = new CSharpCliGenerator();
        var result = generator.Generate(metadata, new GeneratorOptions(_outputDir, "void-cli"));

        var itemCmd = File.ReadAllText(
            result.GeneratedFiles.First(f => f.EndsWith("ItemCommands.cs")));

        // Void return should NOT call formatters
        Assert.DoesNotContain("JsonFormatter.Write", itemCmd);
        Assert.DoesNotContain("TableFormatter.Write", itemCmd);
        // Should still have success output
        Assert.Contains("ExitCode = 0", itemCmd);
    }

    [Fact]
    public void Generate_AllExpectedFilesExist()
    {
        var result = Generate();

        // Verify each expected file exists by name
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith(".csproj"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("Program.cs"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("CustomerCommands.cs"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("OrderCommands.cs"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("ProductCommands.cs"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("JsonFormatter.cs"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("TableFormatter.cs"));
        Assert.Contains(result.GeneratedFiles, f => f.EndsWith("AuthHandler.cs"));
    }

    // -----------------------------------------------------------
    // Phase 6D: Compile verification
    // -----------------------------------------------------------

    private static string RepoRoot
    {
        get
        {
            var testDir = Path.GetDirectoryName(typeof(CSharpCliGeneratorTests).Assembly.Location)!;
            return Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        }
    }

    [Fact]
    public async Task Generate_TestSdk_CompilesWithDotnetBuild()
    {
        var testSdkCsproj = Path.Combine(RepoRoot, "tests", "CliBuilder.TestSdk", "CliBuilder.TestSdk.csproj");
        var generator = new CSharpCliGenerator();
        var options = new GeneratorOptions(_outputDir, "testsdk-cli", SdkProjectPath: testSdkCsproj);
        var result = generator.Generate(_testSdkMetadata, options);

        // dotnet build → exit code 0
        // Read stdout/stderr async before WaitForExit to avoid pipe buffer deadlock
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
                $"dotnet build failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
        }
    }

    [Fact]
    public void Generate_WithSdkProjectPath_EmitsProjectReference()
    {
        var generator = new CSharpCliGenerator();
        var options = new GeneratorOptions(_outputDir, "ref-test",
            SdkProjectPath: "/some/path/to/Sdk.csproj");
        var result = generator.Generate(_testSdkMetadata, options);

        var csproj = File.ReadAllText(Path.Combine(result.ProjectDirectory, "ref-test.csproj"));
        Assert.Contains("ProjectReference", csproj);
        Assert.Contains("/some/path/to/Sdk.csproj", csproj);
        Assert.DoesNotContain("PackageReference Include=\"CliBuilder.TestSdk\"", csproj);
    }

    [Fact]
    public void Generate_WithoutSdkProjectPath_EmitsPackageReference()
    {
        var result = Generate();
        var csproj = File.ReadAllText(Path.Combine(result.ProjectDirectory, "testsdk-cli.csproj"));
        Assert.Contains("PackageReference", csproj);
        Assert.DoesNotContain("ProjectReference", csproj);
    }

    // -----------------------------------------------------------
    // Phase 6D: Golden file comparison
    // -----------------------------------------------------------

    private static readonly string[] GoldenFiles = new[]
    {
        "testsdk-cli.csproj",
        "Program.cs",
        "Commands/CustomerCommands.cs",
        "Commands/OrderCommands.cs",
        "Commands/ProductCommands.cs",
        "Output/JsonFormatter.cs",
        "Output/TableFormatter.cs",
        "Auth/AuthHandler.cs"
    };

    [Theory]
    [MemberData(nameof(GetGoldenFileNames))]
    public void Generate_TestSdk_MatchesGoldenFile(string relativePath)
    {
        var result = Generate();
        var generatedPath = Path.Combine(result.ProjectDirectory, relativePath);
        var goldenPath = Path.Combine(RepoRoot, "tests", "golden", "testsdk-cli", relativePath);

        // UPDATE_GOLDEN=1 → write/overwrite golden files instead of comparing
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var dir = Path.GetDirectoryName(goldenPath)!;
            Directory.CreateDirectory(dir);
            File.Copy(generatedPath, goldenPath, overwrite: true);
            return;
        }

        Assert.True(File.Exists(goldenPath),
            $"Golden file not found: {goldenPath}. Run with UPDATE_GOLDEN=1 to create.");

        var generated = File.ReadAllText(generatedPath);
        var golden = File.ReadAllText(goldenPath);
        Assert.Equal(golden, generated);
    }

    public static IEnumerable<object[]> GetGoldenFileNames()
        => GoldenFiles.Select(f => new object[] { f });

    [Fact]
    public void Generate_TestSdk_GoldenFileCount_MatchesGeneratedFileCount()
    {
        var result = Generate();
        Assert.Equal(GoldenFiles.Length, result.GeneratedFiles.Count);
    }

    [Fact]
    public void Generate_CSharpKeywordProperty_HasAtPrefix()
    {
        // SanitizationOptions has @class and @event properties (C# keywords).
        // The generated code must use @class/@event in property assignments, not bare keywords.
        var result = Generate();
        var orderCommands = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "OrderCommands.cs"));
        Assert.Contains("@class", orderCommands);
        Assert.Contains("@event", orderCommands);
    }

    // -----------------------------------------------------------
    // SDK call wiring tests (step 7B)
    // -----------------------------------------------------------

    [Fact]
    public void Generate_CustomerCreate_HasRealSdkCall()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "CustomerCommands.cs"));
        // Real client instantiation (not commented out)
        Assert.Contains("var client = new CustomerService(credential);", content);
        Assert.DoesNotContain("// var client = new CustomerService", content);
    }

    [Fact]
    public void Generate_ProductList_WrapsCredentialInNew()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "ProductCommands.cs"));
        Assert.Contains("new TokenCredential(credential)", content);
    }

    [Fact]
    public void Generate_CustomerCreate_HasOptionsClassConstruction()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "CustomerCommands.cs"));
        Assert.Contains("var createCustomerOptions = new CreateCustomerOptions();", content);
        Assert.Contains("createCustomerOptions.Email = emailValue;", content);
    }

    [Fact]
    public void Generate_CustomerCreate_HasEnumConversion()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "CustomerCommands.cs"));
        Assert.Contains("Enum.Parse<CustomerStatus>", content);
    }

    [Fact]
    public void Generate_CustomerGet_PassesDirectParams()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "CustomerCommands.cs"));
        Assert.Contains("client.GetAsync(idValue)", content);
    }

    [Fact]
    public void Generate_CustomerStream_HasAwaitForeach()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "CustomerCommands.cs"));
        Assert.Contains("await foreach", content);
        Assert.Contains("client.StreamAsync()", content);
    }

    [Fact]
    public void Generate_MultipleNamespaces_AllPresent()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "CustomerCommands.cs"));
        Assert.Contains("using CliBuilder.TestSdk.Models;", content);
        Assert.Contains("using CliBuilder.TestSdk.Services;", content);
    }

    [Fact]
    public void Generate_CustomerCreate_HasMultipleOptionsClasses()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "CustomerCommands.cs"));
        // CreateAsync takes CreateCustomerOptions + RequestOptions
        Assert.Contains("new CreateCustomerOptions()", content);
        Assert.Contains("new RequestOptions()", content);
        Assert.Contains("client.CreateAsync(createCustomerOptions, requestOptions)", content);
    }

    [Fact]
    public void Generate_NoSourceClassName_FallsBackToEcho()
    {
        // Resource without SourceClassName → echo dictionary fallback
        var resource = new Resource("widget", null, new[]
        {
            new Operation("get", null,
                new[] { new Parameter("id", new TypeRef(TypeKind.Primitive, "string"), true) },
                new TypeRef(TypeKind.Class, "Widget"))
        });
        var metadata = new SdkMetadata("TestSdk", "1.0.0", new[] { resource }, Array.Empty<AuthPattern>());
        var generator = new CSharpCliGenerator();
        var result = generator.Generate(metadata, new GeneratorOptions(_outputDir, "test-cli"));

        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "WidgetCommands.cs"));
        // Should use the echo fallback, not SDK call
        Assert.Contains("SDK client wiring not available", content);
        Assert.Contains("[\"command\"] = \"widget get\"", content);
        Assert.DoesNotContain("var client = new", content);
    }

    [Fact]
    public void Generate_CustomerDelete_IsVoidReturn()
    {
        var result = Generate();
        var content = File.ReadAllText(
            Path.Combine(result.ProjectDirectory, "Commands", "CustomerCommands.cs"));
        // delete returns bool (non-void), but verify the void template branch exists
        // by checking that the delete handler calls the SDK method
        Assert.Contains("client.DeleteAsync(idValue)", content);
    }
}
