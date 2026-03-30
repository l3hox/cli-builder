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
    // MapTypeName — forCliParam
    // -----------------------------------------------------------

    [Fact]
    public void MapTypeName_ClassType_ForCliParam_ReturnsString()
    {
        var type = new TypeRef(TypeKind.Class, "ChatCompletionOptions");
        Assert.Equal("string", ModelMapper.MapTypeName(type, forCliParam: true));
    }

    [Fact]
    public void MapTypeName_ClassType_ForReturn_PreservesSdkName()
    {
        var type = new TypeRef(TypeKind.Class, "ChatCompletionOptions");
        Assert.Equal("ChatCompletionOptions", ModelMapper.MapTypeName(type, forCliParam: false));
    }

    [Fact]
    public void MapTypeName_GenericType_ForCliParam_ReturnsString()
    {
        var inner = new TypeRef(TypeKind.Class, "ResponseItem");
        var type = new TypeRef(TypeKind.Generic, "IEnumerable",
            GenericArguments: new[] { inner });
        Assert.Equal("string", ModelMapper.MapTypeName(type, forCliParam: true));
    }

    [Fact]
    public void MapTypeName_GenericType_ForReturn_PreservesFullType()
    {
        var inner = new TypeRef(TypeKind.Class, "ResponseItem");
        var type = new TypeRef(TypeKind.Generic, "IEnumerable",
            GenericArguments: new[] { inner });
        Assert.Equal("IEnumerable<ResponseItem>", ModelMapper.MapTypeName(type, forCliParam: false));
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

    // -----------------------------------------------------------
    // Helpers: PascalToCamelCase, KebabToCamelCase (step 7A)
    // -----------------------------------------------------------

    [Theory]
    [InlineData("CreateCustomerOptions", "createCustomerOptions")]
    [InlineData("RequestOptions", "requestOptions")]
    [InlineData("A", "a")]
    [InlineData("", "")]
    public void PascalToCamelCase_Works(string input, string expected)
    {
        Assert.Equal(expected, ModelMapper.PascalToCamelCase(input));
    }

    [Theory]
    [InlineData("id", "id")]
    [InlineData("credit-limit", "creditLimit")]
    [InlineData("api-key", "apiKey")]
    [InlineData("", "_param")]
    public void KebabToCamelCase_Works(string input, string expected)
    {
        Assert.Equal(expected, ModelMapper.KebabToCamelCase(input));
    }

    // -----------------------------------------------------------
    // ConstructorAuthExpression (step 7A)
    // -----------------------------------------------------------

    [Fact]
    public void MapResource_StringAuth_CredentialExpression()
    {
        var resource = new Resource("customer", null, new List<Operation>(),
            SourceClassName: "CustomerService", SourceNamespace: "Test.Services",
            ConstructorAuthTypeName: "string");
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.Equal("credential", model.Resources[0].ConstructorAuthExpression);
    }

    [Fact]
    public void MapResource_TokenCredentialAuth_WrapsInNew()
    {
        var resource = new Resource("product", null, new List<Operation>(),
            SourceClassName: "ProductApi", SourceNamespace: "Test.Services",
            ConstructorAuthTypeName: "TokenCredential", ConstructorAuthTypeNamespace: "Test.Models");
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.Equal("new TokenCredential(credential)", model.Resources[0].ConstructorAuthExpression);
    }

    [Fact]
    public void MapResource_NullAuth_DefaultsToCredential()
    {
        var resource = new Resource("thing", null, new List<Operation>());
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        Assert.Equal("credential", model.Resources[0].ConstructorAuthExpression);
    }

    // -----------------------------------------------------------
    // RequiredNamespaces (step 7A)
    // -----------------------------------------------------------

    [Fact]
    public void MapResource_CollectsNamespacesFromOperations()
    {
        var optionsType = new TypeRef(TypeKind.Class, "MyOptions",
            Properties: new[] { new Parameter("X", new TypeRef(TypeKind.Primitive, "string"), true) },
            Namespace: "Sdk.Options");
        var op = new Operation("do", null,
            new[] { new Parameter("opts", optionsType, true) },
            new TypeRef(TypeKind.Primitive, "void"),
            SourceMethodName: "DoAsync");
        var resource = new Resource("thing", null, new[] { op },
            SourceClassName: "ThingService", SourceNamespace: "Sdk.Services",
            ConstructorAuthTypeName: "TokenCredential", ConstructorAuthTypeNamespace: "Sdk.Auth");
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        var ns = model.Resources[0].RequiredNamespaces!;
        Assert.Contains("Sdk.Services", ns);
        Assert.Contains("Sdk.Options", ns);
        Assert.Contains("Sdk.Auth", ns);
    }

    // -----------------------------------------------------------
    // MethodParams (step 7A)
    // -----------------------------------------------------------

    [Fact]
    public void MapOperation_BuildsMethodParams_ForOptionsClass()
    {
        var optionsType = new TypeRef(TypeKind.Class, "CreateOptions",
            Properties: new[] { new Parameter("Name", new TypeRef(TypeKind.Primitive, "string"), true) },
            Namespace: "Sdk.Models");
        var op = new Operation("create", null,
            new[] { new Parameter("options", optionsType, true) },
            new TypeRef(TypeKind.Primitive, "void"),
            SourceMethodName: "CreateAsync");
        var resource = new Resource("thing", null, new[] { op },
            SourceClassName: "ThingService", SourceNamespace: "Sdk");
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        var mp = model.Resources[0].Operations[0].MethodParams!;
        Assert.Single(mp);
        Assert.True(mp[0].IsOptionsClass);
        Assert.Equal("CreateOptions", mp[0].TypeName);
        Assert.Equal("createOptions", mp[0].ArgExpression);
        Assert.Equal("Sdk.Models", mp[0].Namespace);
    }

    [Fact]
    public void MapOperation_BuildsMethodParams_ForDirectParam()
    {
        var op = new Operation("get", null,
            new[] { new Parameter("id", new TypeRef(TypeKind.Primitive, "string"), true) },
            new TypeRef(TypeKind.Class, "Customer"),
            SourceMethodName: "GetAsync");
        var resource = new Resource("thing", null, new[] { op },
            SourceClassName: "ThingService", SourceNamespace: "Sdk");
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        var mp = model.Resources[0].Operations[0].MethodParams!;
        Assert.Single(mp);
        Assert.False(mp[0].IsOptionsClass);
        Assert.Equal("idValue", mp[0].ArgExpression);
    }

    [Fact]
    public void MapOperation_BuildsMethodParams_MixedOrder()
    {
        var optionsType = new TypeRef(TypeKind.Class, "UpdateOptions",
            Properties: new[] { new Parameter("Name", new TypeRef(TypeKind.Primitive, "string"), true) },
            Namespace: "Sdk.Models");
        var requestType = new TypeRef(TypeKind.Class, "RequestOptions",
            Properties: new[] { new Parameter("Key", new TypeRef(TypeKind.Primitive, "string"), false) },
            Namespace: "Sdk.Models");
        var op = new Operation("update", null,
            new Parameter[] {
                new("options", optionsType, true),
                new("requestOptions", requestType, false),
            },
            new TypeRef(TypeKind.Primitive, "void"),
            SourceMethodName: "UpdateAsync");
        var resource = new Resource("thing", null, new[] { op },
            SourceClassName: "ThingService", SourceNamespace: "Sdk");
        var metadata = MinimalMetadata(resources: new[] { resource });
        var (model, _) = ModelMapper.Build(metadata, new GeneratorOptions("/tmp/out", "test-cli"));

        var mp = model.Resources[0].Operations[0].MethodParams!;
        Assert.Equal(2, mp.Count);
        Assert.Equal("updateOptions", mp[0].ArgExpression);
        Assert.Equal("requestOptions", mp[1].ArgExpression);
        Assert.True(mp[0].IsOptionsClass);
        Assert.True(mp[1].IsOptionsClass);
    }
}
