using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Models;

namespace CliBuilder.Core.Tests;

public class DotNetAdapterTests
{
    private static readonly string TestSdkAssemblyPath = GetTestSdkAssemblyPath();

    private static string GetTestSdkAssemblyPath()
    {
        // TestSdk is built as part of the solution but NOT referenced by tests.
        // Find it relative to the test output directory, handling Debug/Release.
        var testDir = Path.GetDirectoryName(typeof(DotNetAdapterTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var configuration = testDir.Contains(Path.Combine("bin", "Release")) ? "Release" : "Debug";
        var sdkPath = Path.Combine(repoRoot,
            "tests", "CliBuilder.TestSdk", "bin", configuration, "net8.0", "CliBuilder.TestSdk.dll");

        if (!File.Exists(sdkPath))
            throw new InvalidOperationException(
                $"TestSdk assembly not found at: {sdkPath}. Ensure the solution is built before running tests.");

        return sdkPath;
    }

    private AdapterResult ExtractTestSdk()
    {
        var adapter = new DotNetAdapter();
        return adapter.Extract(new AdapterOptions(TestSdkAssemblyPath));
    }

    // -------------------------------------------------------
    // Resource discovery
    // -------------------------------------------------------

    [Fact]
    public void Discovers_ExactlyThreeResources()
    {
        var result = ExtractTestSdk();
        // customer (CustomerService), order (OrderClient), product (ProductApi)
        // InternalHelper excluded (no matching suffix)
        // CustomerApiService excluded (noun collision → error diagnostic)
        var resourceNames = result.Metadata.Resources.Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Equal(3, resourceNames.Count);
        Assert.Contains("customer", resourceNames);
        Assert.Contains("order", resourceNames);
        Assert.Contains("product", resourceNames);
    }

    [Fact]
    public void DoesNotDiscover_InternalHelper()
    {
        var result = ExtractTestSdk();
        // InternalHelper has a constructor and async methods, but no matching suffix
        Assert.DoesNotContain(result.Metadata.Resources,
            r => r.Name.Contains("internal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Emits_NounCollision_Error_ForShippingClasses()
    {
        var result = ExtractTestSdk();
        var collision = result.Diagnostics.FirstOrDefault(d => d.Code == "CB202");
        Assert.NotNull(collision);
        Assert.Equal(DiagnosticSeverity.Error, collision.Severity);
        Assert.Contains("shipping", collision.Message, StringComparison.OrdinalIgnoreCase);
        // Colliding resources are excluded entirely
        Assert.DoesNotContain(result.Metadata.Resources, r => r.Name == "shipping");
    }

    // -------------------------------------------------------
    // Operation discovery
    // -------------------------------------------------------

    [Fact]
    public void CustomerService_Has_CreateOperation()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        Assert.Contains(customer.Operations, o => o.Name == "create");
    }

    [Fact]
    public void CustomerService_Has_GetOperation()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        Assert.Contains(customer.Operations, o => o.Name == "get");
    }

    [Fact]
    public void CustomerService_Has_ListOperation()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        Assert.Contains(customer.Operations, o => o.Name == "list");
    }

    [Fact]
    public void CustomerService_Has_DeleteOperation()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        Assert.Contains(customer.Operations, o => o.Name == "delete");
    }

    [Fact]
    public void CustomerService_Has_StreamOperation()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        Assert.Contains(customer.Operations, o => o.Name == "stream");
    }

    [Fact]
    public void CustomerService_Has_GetMetadataOperation()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        Assert.Contains(customer.Operations, o => o.Name == "get-metadata");
    }

    [Fact]
    public void SyncAsyncPair_DoesNotEmit_VerbCollision()
    {
        // Get + GetAsync is a sync+async pair — should be handled silently, not CB201
        var result = ExtractTestSdk();
        var collisions = result.Diagnostics.Where(d => d.Code == "CB201").ToList();
        Assert.Empty(collisions);
    }

    [Fact]
    public void Emits_OverloadDisambiguated_Diagnostic_ForCreateAsync()
    {
        var result = ExtractTestSdk();
        var overload = result.Diagnostics.FirstOrDefault(d => d.Code == "CB203");
        Assert.NotNull(overload);
        Assert.Equal(DiagnosticSeverity.Info, overload.Severity);
    }

    [Fact]
    public void CancellationToken_IsExcluded_FromParameters()
    {
        var result = ExtractTestSdk();
        Assert.NotEmpty(result.Metadata.Resources);
        foreach (var resource in result.Metadata.Resources)
        {
            Assert.NotEmpty(resource.Operations);
            foreach (var op in resource.Operations)
            {
                Assert.DoesNotContain(op.Parameters, p => p.Name == "cancellationToken");
            }
        }
    }

    // -------------------------------------------------------
    // Type extraction
    // -------------------------------------------------------

    [Fact]
    public void CreateAsync_ReturnType_UnwrapsTaskToCustomer()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        Assert.Equal(TypeKind.Class, create.ReturnType.Kind);
        Assert.Equal("Customer", create.ReturnType.Name);
    }

    [Fact]
    public void ListAsync_ReturnType_UnwrapsTaskToGenericList()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var list = customer.Operations.First(o => o.Name == "list");
        Assert.Equal(TypeKind.Generic, list.ReturnType.Kind);
        Assert.Equal("List", list.ReturnType.Name);
        Assert.NotNull(list.ReturnType.GenericArguments);
        Assert.Single(list.ReturnType.GenericArguments);
        Assert.Equal("Customer", list.ReturnType.GenericArguments[0].Name);
    }

    [Fact]
    public void DeleteAsync_ReturnType_UnwrapsValueTaskToPrimitiveBool()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var delete = customer.Operations.First(o => o.Name == "delete");
        Assert.Equal(TypeKind.Primitive, delete.ReturnType.Kind);
        Assert.Equal("bool", delete.ReturnType.Name);
    }

    [Fact]
    public void OrderClient_CreateAsync_UnwrapsClientResultToOrder()
    {
        var result = ExtractTestSdk();
        var order = result.Metadata.Resources.First(r => r.Name == "order");
        var create = order.Operations.First(o => o.Name == "create");
        // Task<ClientResult<Order>> should double-unwrap: Task<T> then ClientResult<T>
        Assert.Equal(TypeKind.Class, create.ReturnType.Kind);
        Assert.Equal("Order", create.ReturnType.Name);
    }

    [Fact]
    public void GetMetadataAsync_ReturnType_IsDictionary()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var getMeta = customer.Operations.First(o => o.Name == "get-metadata");
        Assert.Equal(TypeKind.Dictionary, getMeta.ReturnType.Kind);
    }

    [Fact]
    public void StreamAsync_ReturnType_UnwrapsIAsyncEnumerable()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var stream = customer.Operations.First(o => o.Name == "stream");
        // IAsyncEnumerable<Customer> unwraps to Customer
        Assert.Equal(TypeKind.Class, stream.ReturnType.Kind);
        Assert.Equal("Customer", stream.ReturnType.Name);
    }

    [Fact]
    public void ListAsync_Parameter_CursorIsNullable()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var list = customer.Operations.First(o => o.Name == "list");
        var cursor = list.Parameters.First(p => p.Name == "cursor");
        Assert.True(cursor.Type.IsNullable);
    }

    [Fact]
    public void CreateCustomerOptions_InitialStatus_ExtractsEnumValues()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        // CreateCustomerOptions has InitialStatus of type CustomerStatus?
        var options = create.Parameters.FirstOrDefault(p => p.Type.Kind == TypeKind.Class);
        Assert.NotNull(options);
        Assert.NotNull(options.Type.Properties);
        var statusProp = options.Type.Properties.FirstOrDefault(p => p.Name == "InitialStatus");
        Assert.NotNull(statusProp);
        Assert.Equal(TypeKind.Enum, statusProp.Type.Kind);
        Assert.NotNull(statusProp.Type.EnumValues);
        Assert.Contains("Active", statusProp.Type.EnumValues);
        Assert.Contains("Inactive", statusProp.Type.EnumValues);
        Assert.Contains("Suspended", statusProp.Type.EnumValues);
    }

    // -------------------------------------------------------
    // Auth pattern detection
    // -------------------------------------------------------

    [Fact]
    public void Detects_ApiKey_AuthPattern_FromCustomerService()
    {
        var result = ExtractTestSdk();
        Assert.Contains(result.Metadata.AuthPatterns, a =>
            a.Type == AuthType.ApiKey && a.ParameterName == "apiKey");
    }

    [Fact]
    public void Detects_BearerToken_AuthPattern_FromProductApi()
    {
        var result = ExtractTestSdk();
        Assert.Contains(result.Metadata.AuthPatterns, a =>
            a.Type == AuthType.BearerToken && a.ParameterName == "credential");
    }

    // -------------------------------------------------------
    // Naming conventions
    // -------------------------------------------------------

    [Fact]
    public void ResourceNames_AreKebabCase()
    {
        var result = ExtractTestSdk();
        Assert.NotEmpty(result.Metadata.Resources);
        foreach (var resource in result.Metadata.Resources)
        {
            Assert.Matches("^[a-z][a-z0-9-]*$", resource.Name);
        }
    }

    [Fact]
    public void OperationNames_AreKebabCase_WithAsyncStripped()
    {
        var result = ExtractTestSdk();
        Assert.NotEmpty(result.Metadata.Resources);
        foreach (var resource in result.Metadata.Resources)
        {
            Assert.NotEmpty(resource.Operations);
            foreach (var op in resource.Operations)
            {
                Assert.Matches("^[a-z][a-z0-9-]*$", op.Name);
                Assert.DoesNotContain("async", op.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // -------------------------------------------------------
    // Streaming marker
    // -------------------------------------------------------

    [Fact]
    public void StreamAsync_IsMarkedAsStreaming()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var stream = customer.Operations.First(o => o.Name == "stream");
        Assert.True(stream.IsStreaming);
    }

    [Fact]
    public void CreateAsync_IsNotStreaming()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        Assert.False(create.IsStreaming);
    }

    // -------------------------------------------------------
    // Parameter.Required
    // -------------------------------------------------------

    [Fact]
    public void RequiredParameter_IsMarkedRequired()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var get = customer.Operations.First(o => o.Name == "get");
        var id = get.Parameters.First(p => p.Name == "id");
        Assert.True(id.Required);
    }

    [Fact]
    public void OptionalParameter_IsNotRequired()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var list = customer.Operations.First(o => o.Name == "list");
        var limit = list.Parameters.First(p => p.Name == "limit");
        Assert.False(limit.Required);
    }

    // -------------------------------------------------------
    // SanitizationOptions keyword properties
    // -------------------------------------------------------

    [Fact]
    public void SanitizationOptions_KeywordProperties_AreExtracted()
    {
        var result = ExtractTestSdk();
        var order = result.Metadata.Resources.First(r => r.Name == "order");
        var process = order.Operations.First(o => o.Name == "process");
        var optionsParam = process.Parameters.FirstOrDefault(p => p.Type.Kind == TypeKind.Class);
        Assert.NotNull(optionsParam);
        Assert.NotNull(optionsParam.Type.Properties);
        // Reflection strips the @ prefix — properties appear as "class" and "event"
        Assert.Contains(optionsParam.Type.Properties, p => p.Name == "class");
        Assert.Contains(optionsParam.Type.Properties, p => p.Name == "event");
    }

    // -------------------------------------------------------
    // NestedOptions
    // -------------------------------------------------------

    [Fact]
    public void NestedOptions_ShippingAddress_IsExtractedAsClass()
    {
        var result = ExtractTestSdk();
        var order = result.Metadata.Resources.First(r => r.Name == "order");
        var update = order.Operations.First(o => o.Name == "update");
        var optionsParam = update.Parameters.FirstOrDefault(p => p.Type.Kind == TypeKind.Class);
        Assert.NotNull(optionsParam);
        Assert.NotNull(optionsParam.Type.Properties);
        var address = optionsParam.Type.Properties.FirstOrDefault(p => p.Name == "ShippingAddress");
        Assert.NotNull(address);
        Assert.Equal(TypeKind.Class, address.Type.Kind);
        Assert.Equal("Address", address.Type.Name);
        // ShippingAddress is Address? (nullable) — must be detected
        Assert.True(address.Type.IsNullable);
    }

    // -------------------------------------------------------
    // Property-level nullability and Required
    // -------------------------------------------------------

    [Fact]
    public void CreateCustomerOptions_NullableProperty_IsMarkedNullable()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        var optionsParam = create.Parameters.FirstOrDefault(p => p.Type.Kind == TypeKind.Class);
        Assert.NotNull(optionsParam);
        Assert.NotNull(optionsParam.Type.Properties);
        // Name is string? — nullable
        var name = optionsParam.Type.Properties.FirstOrDefault(p => p.Name == "Name");
        Assert.NotNull(name);
        Assert.True(name.Type.IsNullable);
    }

    [Fact]
    public void CreateCustomerOptions_NonNullableProperty_IsNotNullable()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        var optionsParam = create.Parameters.FirstOrDefault(p => p.Type.Kind == TypeKind.Class);
        Assert.NotNull(optionsParam);
        Assert.NotNull(optionsParam.Type.Properties);
        // Email is string (non-nullable)
        var email = optionsParam.Type.Properties.FirstOrDefault(p => p.Name == "Email");
        Assert.NotNull(email);
        Assert.False(email.Type.IsNullable);
    }

    [Fact]
    public void CreateCustomerOptions_NonNullableProperty_IsRequired()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        var optionsParam = create.Parameters.FirstOrDefault(p => p.Type.Kind == TypeKind.Class);
        Assert.NotNull(optionsParam);
        Assert.NotNull(optionsParam.Type.Properties);
        // Email is string (non-nullable, no default) → required
        var email = optionsParam.Type.Properties.FirstOrDefault(p => p.Name == "Email");
        Assert.NotNull(email);
        Assert.True(email.Required);
    }

    [Fact]
    public void CreateCustomerOptions_NullableProperty_IsNotRequired()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        var optionsParam = create.Parameters.FirstOrDefault(p => p.Type.Kind == TypeKind.Class);
        Assert.NotNull(optionsParam);
        Assert.NotNull(optionsParam.Type.Properties);
        // Name is string? (nullable) → not required
        var name = optionsParam.Type.Properties.FirstOrDefault(p => p.Name == "Name");
        Assert.NotNull(name);
        Assert.False(name.Required);
    }

    // -------------------------------------------------------
    // Negative diagnostic tests
    // -------------------------------------------------------

    [Fact]
    public void ProductApi_EmitsNoDiagnostics()
    {
        // ProductApi has one method, no collisions, no ambiguity — should be clean
        var result = ExtractTestSdk();
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Message.Contains("ProductApi", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------
    // TypeRef.Namespace (step 7A)
    // -------------------------------------------------------

    [Fact]
    public void ClassTypeRef_HasNamespace()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        var optionsParam = create.Parameters.First(p => p.Type.Kind == TypeKind.Class);
        Assert.Equal("CliBuilder.TestSdk.Models", optionsParam.Type.Namespace);
    }

    [Fact]
    public void EnumTypeRef_HasNamespace()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        var optionsParam = create.Parameters.First(p => p.Type.Kind == TypeKind.Class);
        var statusProp = optionsParam.Type.Properties!.First(p => p.Name == "InitialStatus");
        Assert.Equal("CliBuilder.TestSdk.Models", statusProp.Type.Namespace);
    }

    [Fact]
    public void ReturnTypeRef_HasNamespace()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        var create = customer.Operations.First(o => o.Name == "create");
        Assert.Equal("CliBuilder.TestSdk.Models", create.ReturnType.Namespace);
    }

    // -------------------------------------------------------
    // Constructor auth type (step 7A)
    // -------------------------------------------------------

    [Fact]
    public void CustomerService_ConstructorAuthType_IsString()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        Assert.NotNull(customer.ConstructorParams);
        Assert.Single(customer.ConstructorParams!);
        Assert.Equal("apiKey", customer.ConstructorParams![0].Name);
        Assert.Equal("string", customer.ConstructorParams![0].TypeName);
        Assert.True(customer.ConstructorParams![0].IsAuth);
    }

    [Fact]
    public void ProductApi_ConstructorAuthType_IsTokenCredential()
    {
        var result = ExtractTestSdk();
        var product = result.Metadata.Resources.First(r => r.Name == "product");
        Assert.NotNull(product.ConstructorParams);
        Assert.Single(product.ConstructorParams!);
        Assert.Equal("credential", product.ConstructorParams![0].Name);
        Assert.Equal("TokenCredential", product.ConstructorParams![0].TypeName);
        Assert.Equal("CliBuilder.TestSdk.Models", product.ConstructorParams![0].TypeNamespace);
        Assert.True(product.ConstructorParams![0].IsAuth);
    }

    [Fact]
    public void OrderClient_ConstructorAuthType_IsString()
    {
        var result = ExtractTestSdk();
        var order = result.Metadata.Resources.First(r => r.Name == "order");
        Assert.NotNull(order.ConstructorParams);
        Assert.Single(order.ConstructorParams!);
        Assert.Equal("apiKey", order.ConstructorParams![0].Name);
        Assert.Equal("string", order.ConstructorParams![0].TypeName);
        Assert.True(order.ConstructorParams![0].IsAuth);
    }
}
