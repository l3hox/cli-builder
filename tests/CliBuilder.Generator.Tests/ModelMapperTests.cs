using System.Text.Json;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;

namespace CliBuilder.Generator.Tests;

public class ModelMapperTests
{
    private static SdkMetadata MinimalMetadata(
        string name = "TestSdk",
        string version = "1.0.0",
        IReadOnlyList<Resource>? resources = null,
        IReadOnlyList<AuthPattern>? authPatterns = null)
    {
        return new SdkMetadata(
            name,
            version,
            resources ?? new List<Resource>(),
            authPatterns ?? new List<AuthPattern>());
    }

    private static Resource MakeResource(string name, string? description = null)
    {
        return new Resource(name, description, new List<Operation>());
    }

    // -----------------------------------------------------------
    // Name conversion
    // -----------------------------------------------------------

    [Theory]
    [InlineData("customer", "Customer")]
    [InlineData("payment-intent", "PaymentIntent")]
    [InlineData("order", "Order")]
    [InlineData("get-metadata", "GetMetadata")]
    public void Build_ConvertsKebabCaseToPascalCase(string kebab, string expectedPascal)
    {
        var metadata = MinimalMetadata(resources: new[] { MakeResource(kebab) });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));
        Assert.Equal(expectedPascal, model.Resources[0].ClassName);
    }

    // -----------------------------------------------------------
    // Scriban syntax sanitization
    // -----------------------------------------------------------

    [Fact]
    public void Build_SanitizesScribanSyntaxInDescriptions()
    {
        var resource = new Resource(
            "test",
            "Use {{ env 'SECRET' }} here",
            new List<Operation>());
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        // Description must not contain raw {{ }} that Scriban would execute
        Assert.DoesNotContain("{{", model.Resources[0].Description);
        Assert.DoesNotContain("}}", model.Resources[0].Description);
    }

    [Fact]
    public void Build_NullDescriptionRemainsNull()
    {
        var resource = MakeResource("test", description: null);
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));
        Assert.Null(model.Resources[0].Description);
    }

    // -----------------------------------------------------------
    // Path safety
    // -----------------------------------------------------------

    [Theory]
    [InlineData("../etc")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("..")]
    public void Build_PathUnsafeResourceName_EmitsDiagnostic(string unsafeName)
    {
        var resource = MakeResource(unsafeName);
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, diagnostics) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        // Should emit a diagnostic about unsafe name
        Assert.Contains(diagnostics, d => d.Code == "CB204");
        // ClassName must be safe (no path separators)
        Assert.DoesNotContain("/", model.Resources[0].ClassName);
        Assert.DoesNotContain("\\", model.Resources[0].ClassName);
        Assert.DoesNotContain("..", model.Resources[0].ClassName);
    }

    // -----------------------------------------------------------
    // Identifier validation
    // -----------------------------------------------------------

    [Fact]
    public void Build_CSharpKeywordResourceName_EmitsDiagnostic()
    {
        // "class" as a resource name should be sanitized
        var resource = MakeResource("class");
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, diagnostics) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.Contains(diagnostics, d => d.Code == "CB004");
    }

    // -----------------------------------------------------------
    // Auth mapping
    // -----------------------------------------------------------

    [Fact]
    public void Build_MapsAuthPatterns()
    {
        var auth = new AuthPattern(AuthType.ApiKey, "MY_API_KEY", "apiKey");
        var metadata = MinimalMetadata(authPatterns: new[] { auth });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.NotNull(model.Auth);
        Assert.Equal("MY_API_KEY", model.Auth!.EnvVar);
    }

    [Fact]
    public void Build_NoAuth_AuthIsNull()
    {
        var metadata = MinimalMetadata();
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));
        Assert.Null(model.Auth);
    }

    // -----------------------------------------------------------
    // CLI name derivation
    // -----------------------------------------------------------

    [Fact]
    public void Build_CliNameFromOptions()
    {
        var metadata = MinimalMetadata(name: "OpenAI");
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "openai-cli"));
        Assert.Equal("openai-cli", model.CliName);
    }

    [Fact]
    public void Build_DerivedCliName_IsExactValue()
    {
        var metadata = MinimalMetadata(name: "CliBuilder.TestSdk");
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out"));
        Assert.Equal("clibuilder-testsdk", model.CliName);
    }

    // -----------------------------------------------------------
    // Operation mapping (basic — flattening is 6B)
    // -----------------------------------------------------------

    [Fact]
    public void Build_MapsOperations()
    {
        var op = new Operation("create", "Create a thing", new List<Parameter>(),
            new TypeRef(TypeKind.Class, "Customer"));
        var resource = new Resource("customer", null, new[] { op });
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.Single(model.Resources[0].Operations);
        Assert.Equal("create", model.Resources[0].Operations[0].Name);
        Assert.Equal("Create", model.Resources[0].Operations[0].MethodName);
    }

    // -----------------------------------------------------------
    // MapTypeName — nullable handling
    // -----------------------------------------------------------

    [Fact]
    public void MapTypeName_NullableInt_ReturnsIntQuestionMark()
    {
        var type = new TypeRef(TypeKind.Primitive, "int", IsNullable: true);
        Assert.Equal("int?", ModelMapper.MapTypeName(type));
    }

    [Fact]
    public void MapTypeName_NonNullableInt_ReturnsInt()
    {
        var type = new TypeRef(TypeKind.Primitive, "int", IsNullable: false);
        Assert.Equal("int", ModelMapper.MapTypeName(type));
    }

    [Fact]
    public void MapTypeName_NullableBool_ReturnsBoolQuestionMark()
    {
        var type = new TypeRef(TypeKind.Primitive, "bool", IsNullable: true);
        Assert.Equal("bool?", ModelMapper.MapTypeName(type));
    }

    [Fact]
    public void MapTypeName_NullableString_ReturnsString()
    {
        // string is a reference type — already nullable, no ? needed
        var type = new TypeRef(TypeKind.Primitive, "string", IsNullable: true);
        Assert.Equal("string", ModelMapper.MapTypeName(type));
    }

    [Fact]
    public void MapTypeName_NullableEnum_ReturnsString()
    {
        var type = new TypeRef(TypeKind.Enum, "Status", IsNullable: true);
        // Enums are mapped to "string" for CLI — string is already nullable by reference
        Assert.Equal("string", ModelMapper.MapTypeName(type));
    }

    // -----------------------------------------------------------
    // SanitizeDefaultValue — all branches
    // -----------------------------------------------------------

    [Fact]
    public void SanitizeDefaultValue_Null_ReturnsNull()
    {
        var diagnostics = new List<Diagnostic>();
        var result = ModelMapper.SanitizeDefaultValue(null, new TypeRef(TypeKind.Primitive, "int"), diagnostics);
        Assert.Null(result);
    }

    [Fact]
    public void SanitizeDefaultValue_JsonNull_ReturnsNull()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("null").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "string"), diagnostics);
        Assert.Null(result);
    }

    [Fact]
    public void SanitizeDefaultValue_True_ReturnsTrue()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("true").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "bool"), diagnostics);
        Assert.Equal("true", result);
    }

    [Fact]
    public void SanitizeDefaultValue_False_ReturnsFalse()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("false").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "bool"), diagnostics);
        Assert.Equal("false", result);
    }

    [Fact]
    public void SanitizeDefaultValue_IntNumber_ReturnsRawText()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("42").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "int"), diagnostics);
        Assert.Equal("42", result);
    }

    [Fact]
    public void SanitizeDefaultValue_DecimalNumber_ReturnsSuffixed()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("9.99").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "decimal"), diagnostics);
        Assert.Equal("9.99m", result);
    }

    [Fact]
    public void SanitizeDefaultValue_DoubleNumber_ReturnsSuffixed()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("3.14").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "double"), diagnostics);
        Assert.Equal("3.14d", result);
    }

    [Fact]
    public void SanitizeDefaultValue_FloatNumber_ReturnsSuffixed()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("1.5").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "float"), diagnostics);
        Assert.Equal("1.5f", result);
    }

    [Fact]
    public void SanitizeDefaultValue_String_ReturnsVerbatimLiteral()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("\"hello world\"").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "string"), diagnostics);
        Assert.Equal("@\"hello world\"", result);
    }

    [Fact]
    public void SanitizeDefaultValue_StringWithQuotes_EscapesDoubleQuotes()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("\"say \\\"hi\\\"\"").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Primitive, "string"), diagnostics);
        Assert.Equal("@\"say \"\"hi\"\"\"", result);
    }

    [Fact]
    public void SanitizeDefaultValue_Array_RejectsWithDiagnostic()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("[1, 2, 3]").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Array, "int[]"), diagnostics);
        Assert.Null(result);
        Assert.Contains(diagnostics, d => d.Code == "CB302");
    }

    [Fact]
    public void SanitizeDefaultValue_Object_RejectsWithDiagnostic()
    {
        var diagnostics = new List<Diagnostic>();
        var element = JsonDocument.Parse("{\"key\": \"value\"}").RootElement;
        var result = ModelMapper.SanitizeDefaultValue(element, new TypeRef(TypeKind.Class, "Options"), diagnostics);
        Assert.Null(result);
        Assert.Contains(diagnostics, d => d.Code == "CB302");
    }

    // -----------------------------------------------------------
    // XML injection prevention (csproj)
    // -----------------------------------------------------------

    [Fact]
    public void Build_SdkNameWithXmlMetachars_Sanitized()
    {
        var metadata = MinimalMetadata(name: "Foo\" /><Target Name=\"x\"><Exec Command=\"calc\"/>");
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.DoesNotContain("<", model.SdkPackageName);
        Assert.DoesNotContain(">", model.SdkPackageName);
        Assert.DoesNotContain("\"", model.SdkPackageName);
    }

    [Fact]
    public void Build_SdkVersionWithXmlMetachars_Sanitized()
    {
        var metadata = MinimalMetadata(version: "1.0\"><Exec Command=\"calc\"/>");
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.DoesNotContain("<", model.SdkVersion);
        Assert.DoesNotContain(">", model.SdkVersion);
    }

    [Fact]
    public void Build_CliDescription_SanitizedAgainstScriban()
    {
        var metadata = MinimalMetadata(name: "Evil{{ env 'SECRET' }}Sdk");
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.DoesNotContain("{{", model.CliDescription);
        Assert.DoesNotContain("}}", model.CliDescription);
    }
}
