using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Models;

namespace CliBuilder.Core.Tests;

public class DotNetAdapterTests
{
    private static readonly string TestSdkAssemblyPath = GetTestSdkAssemblyPath();

    private static string GetTestSdkAssemblyPath()
    {
        // TestSdk is built as part of the solution but NOT referenced by tests.
        // Find it relative to the test output directory.
        var testDir = Path.GetDirectoryName(typeof(DotNetAdapterTests).Assembly.Location)!;
        var sdkPath = Path.Combine(testDir, "..", "..", "..", "..", "..",
            "tests", "CliBuilder.TestSdk", "bin", "Debug", "net8.0", "CliBuilder.TestSdk.dll");
        return Path.GetFullPath(sdkPath);
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
    public void Discovers_CustomerService_AsResource()
    {
        var result = ExtractTestSdk();
        Assert.Contains(result.Metadata.Resources, r => r.Name == "customer");
    }

    [Fact]
    public void Discovers_OrderClient_AsResource()
    {
        var result = ExtractTestSdk();
        Assert.Contains(result.Metadata.Resources, r => r.Name == "order");
    }

    [Fact]
    public void Discovers_ProductApi_AsResource()
    {
        var result = ExtractTestSdk();
        Assert.Contains(result.Metadata.Resources, r => r.Name == "product");
    }

    [Fact]
    public void DoesNotDiscover_InternalHelper()
    {
        var result = ExtractTestSdk();
        Assert.DoesNotContain(result.Metadata.Resources, r => r.Name == "internal-helper" || r.Name == "internal");
    }

    [Fact]
    public void Emits_NounCollision_Diagnostic_ForCustomerApiService()
    {
        var result = ExtractTestSdk();
        Assert.Contains(result.Diagnostics, d => d.Code == "CB202");
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
    public void Emits_VerbCollision_Diagnostic_ForGetAndGetAsync()
    {
        var result = ExtractTestSdk();
        Assert.Contains(result.Diagnostics, d => d.Code == "CB201");
    }

    [Fact]
    public void CancellationToken_IsExcluded_FromParameters()
    {
        var result = ExtractTestSdk();
        var customer = result.Metadata.Resources.First(r => r.Name == "customer");
        foreach (var op in customer.Operations)
        {
            Assert.DoesNotContain(op.Parameters, p => p.Name == "cancellationToken");
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
        Assert.Single(list.ReturnType.GenericArguments!);
        Assert.Equal("Customer", list.ReturnType.GenericArguments![0].Name);
    }

    [Fact]
    public void DeleteAsync_ReturnType_UnwrapsTaskToPrimitiveBool()
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
        // Task<ClientResult<Order>> should double-unwrap to Order
        Assert.Equal(TypeKind.Class, create.ReturnType.Kind);
        Assert.Equal("Order", create.ReturnType.Name);
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
        foreach (var resource in result.Metadata.Resources)
        {
            Assert.Matches("^[a-z][a-z0-9-]*$", resource.Name);
        }
    }

    [Fact]
    public void OperationNames_AreKebabCase_WithAsyncStripped()
    {
        var result = ExtractTestSdk();
        foreach (var resource in result.Metadata.Resources)
        {
            foreach (var op in resource.Operations)
            {
                Assert.Matches("^[a-z][a-z0-9-]*$", op.Name);
                Assert.DoesNotContain("async", op.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
