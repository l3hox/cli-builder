using System.Text.Json;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;

namespace CliBuilder.Core.Tests;

public class SdkMetadataSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = SdkMetadataJson.Options;

    [Fact]
    public void SdkMetadata_RoundTrips_ThroughJson()
    {
        var metadata = new SdkMetadata(
            Name: "TestSdk",
            Version: "1.0.0",
            Resources: new List<Resource>
            {
                new Resource(
                    Name: "customer",
                    Description: "Customer resource",
                    Operations: new List<Operation>
                    {
                        new Operation(
                            Name: "list",
                            Description: "List customers",
                            Parameters: new List<Parameter>
                            {
                                new Parameter(
                                    Name: "limit",
                                    Type: new TypeRef(TypeKind.Primitive, "int"),
                                    Required: false,
                                    DefaultValue: JsonDocument.Parse("10").RootElement.Clone()
                                )
                            },
                            ReturnType: new TypeRef(
                                TypeKind.Generic,
                                Name: "StripeList",
                                GenericArguments: new List<TypeRef>
                                {
                                    new TypeRef(TypeKind.Class, "Customer")
                                }
                            )
                        )
                    }
                )
            },
            AuthPatterns: new List<AuthPattern>
            {
                new AuthPattern(
                    Type: AuthType.ApiKey,
                    EnvVar: "TEST_API_KEY",
                    ParameterName: "apiKey"
                )
            }
        );

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<SdkMetadata>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(metadata.Name, deserialized.Name);
        Assert.Equal(metadata.Version, deserialized.Version);
        Assert.Single(deserialized.Resources);
        Assert.Single(deserialized.Resources[0].Operations);
        Assert.Single(deserialized.Resources[0].Operations[0].Parameters);
        Assert.Equal(TypeKind.Generic, deserialized.Resources[0].Operations[0].ReturnType.Kind);
        Assert.Single(deserialized.AuthPatterns);
    }

    [Fact]
    public void TypeRef_WithNestedGenerics_RoundTrips()
    {
        // Two levels deep: PagedResult<List<Customer>>
        var typeRef = new TypeRef(
            TypeKind.Generic,
            Name: "PagedResult",
            GenericArguments: new List<TypeRef>
            {
                new TypeRef(
                    TypeKind.Generic,
                    Name: "List",
                    GenericArguments: new List<TypeRef>
                    {
                        new TypeRef(TypeKind.Class, "Customer")
                    }
                )
            }
        );

        var json = JsonSerializer.Serialize(typeRef, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TypeRef>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(TypeKind.Generic, deserialized.Kind);
        Assert.Single(deserialized.GenericArguments!);
        Assert.Equal(TypeKind.Generic, deserialized.GenericArguments![0].Kind);
        Assert.Single(deserialized.GenericArguments![0].GenericArguments!);
        Assert.Equal("Customer", deserialized.GenericArguments![0].GenericArguments![0].Name);
    }

    [Fact]
    public void TypeRef_WithEnumValues_RoundTrips()
    {
        var typeRef = new TypeRef(
            TypeKind.Enum,
            Name: "CustomerStatus",
            EnumValues: new List<string> { "Active", "Inactive", "Suspended" }
        );

        var json = JsonSerializer.Serialize(typeRef, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TypeRef>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(TypeKind.Enum, deserialized.Kind);
        Assert.Equal(3, deserialized.EnumValues!.Count);
        Assert.Equal("Active", deserialized.EnumValues![0]);
    }

    [Fact]
    public void DefaultValue_PreservesTypeFidelity()
    {
        var intParam = new Parameter(
            Name: "limit",
            Type: new TypeRef(TypeKind.Primitive, "int"),
            Required: false,
            DefaultValue: JsonDocument.Parse("10").RootElement.Clone()
        );

        var stringParam = new Parameter(
            Name: "name",
            Type: new TypeRef(TypeKind.Primitive, "string"),
            Required: false,
            DefaultValue: JsonDocument.Parse("\"10\"").RootElement.Clone()
        );

        var intJson = JsonSerializer.Serialize(intParam, JsonOptions);
        var stringJson = JsonSerializer.Serialize(stringParam, JsonOptions);

        var intDeserialized = JsonSerializer.Deserialize<Parameter>(intJson, JsonOptions);
        var stringDeserialized = JsonSerializer.Deserialize<Parameter>(stringJson, JsonOptions);

        // int 10 and string "10" must be distinguishable after round-trip
        Assert.Equal(JsonValueKind.Number, intDeserialized!.DefaultValue!.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, stringDeserialized!.DefaultValue!.Value.ValueKind);
    }

    [Fact]
    public void DefaultValue_Null_RoundTrips()
    {
        var param = new Parameter(
            Name: "cursor",
            Type: new TypeRef(TypeKind.Primitive, "string"),
            Required: false,
            DefaultValue: null
        );

        var json = JsonSerializer.Serialize(param, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<Parameter>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.DefaultValue);
    }

    [Fact]
    public void NullOptionalFields_RoundTrip_AsNull_NotEmptyCollections()
    {
        // TypeRef with all optional fields null
        var typeRef = new TypeRef(TypeKind.Primitive, "string");

        var json = JsonSerializer.Serialize(typeRef, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TypeRef>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.GenericArguments);
        Assert.Null(deserialized.EnumValues);
        Assert.Null(deserialized.Properties);
        Assert.Null(deserialized.ElementType);
    }

    [Fact]
    public void Adapter_ImplementsInterface_AndThrowsOnMissingFile()
    {
        CliBuilder.Core.Adapters.ISdkAdapter adapter = new CliBuilder.Adapter.DotNet.DotNetAdapter();
        Assert.Throws<FileNotFoundException>(() =>
            adapter.Extract(new AdapterOptions("nonexistent.dll")));
    }

    [Fact]
    public void SkeletonGenerator_ImplementsInterface()
    {
        CliBuilder.Core.Generators.ICliGenerator generator = new CliBuilder.Generator.CSharp.CSharpCliGenerator();
        var metadata = new SdkMetadata("Test", "1.0", new List<Resource>(), new List<AuthPattern>());
        Assert.Throws<NotImplementedException>(() =>
            generator.Generate(metadata, new GeneratorOptions("/tmp")));
    }
}
