using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;

namespace CliBuilder.Generator.Tests;

public class ParameterFlattenerTests
{
    private static Parameter MakePrimitive(string name, string typeName, bool required = true, bool nullable = false)
        => new(name, new TypeRef(TypeKind.Primitive, typeName, IsNullable: nullable), required);

    private static Parameter MakeEnum(string name, string enumName, string[] values, bool required = false)
        => new(name, new TypeRef(TypeKind.Enum, enumName, EnumValues: values), required);

    private static Parameter MakeOptionsClass(string name, IReadOnlyList<Parameter> properties, bool required = true)
        => new(name, new TypeRef(TypeKind.Class, name, Properties: properties), required);

    private static Parameter MakeNestedClass(string name, IReadOnlyList<Parameter> properties)
        => new(name, new TypeRef(TypeKind.Class, name, Properties: properties), Required: false);

    private static IReadOnlyList<Parameter> MakeScalarProps(int count, int requiredCount = 0)
    {
        var props = new List<Parameter>();
        for (int i = 0; i < count; i++)
        {
            var name = $"Prop{(char)('A' + i)}";
            props.Add(MakePrimitive(name, "string", required: i < requiredCount, nullable: i >= requiredCount));
        }
        return props;
    }

    // -----------------------------------------------------------
    // Empty / single param
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_EmptyParameters_ReturnsEmpty()
    {
        var result = ParameterFlattener.Flatten(new List<Parameter>());
        Assert.Empty(result.Parameters);
        Assert.False(result.NeedsJsonInput);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Flatten_SinglePrimitive_ReturnsFlatParam()
    {
        var parameters = new List<Parameter> { MakePrimitive("id", "string") };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Single(result.Parameters);
        Assert.Equal("id", result.Parameters[0].CliFlag);
        Assert.Equal("string", result.Parameters[0].CSharpType);
        Assert.True(result.Parameters[0].IsRequired);
        Assert.False(result.NeedsJsonInput);
    }

    // -----------------------------------------------------------
    // Options class — at threshold boundary
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_OptionsClassExactlyAtThreshold_FlattensAll()
    {
        var props = MakeScalarProps(10);
        var parameters = new List<Parameter> { MakeOptionsClass("Opts", props) };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Equal(10, result.Parameters.Count);
        Assert.False(result.NeedsJsonInput);
    }

    [Fact]
    public void Flatten_OptionsClassAboveThreshold_FlattensFirstTenPlusJsonInput()
    {
        var props = MakeScalarProps(15, requiredCount: 3);
        var parameters = new List<Parameter> { MakeOptionsClass("Opts", props) };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Equal(10, result.Parameters.Count);
        Assert.True(result.NeedsJsonInput);
    }

    [Fact]
    public void Flatten_OptionsClassAtThresholdPlusOne_FlattensFirstTenPlusJsonInput()
    {
        // Boundary: exactly 11 props (threshold + 1), 1 required
        var props = MakeScalarProps(11, requiredCount: 1);
        var parameters = new List<Parameter> { MakeOptionsClass("Opts", props) };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Equal(10, result.Parameters.Count);
        Assert.True(result.NeedsJsonInput);
        // The 1 required prop should be in the first 10 (sorted first)
        Assert.True(result.Parameters[0].IsRequired);
        // No CB301 since the hidden prop (11th) is optional
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CB301");
    }

    // -----------------------------------------------------------
    // Options class — with nested objects
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_OptionsClassWithNested_FlattensAllScalarsPlusJsonInput()
    {
        var scalarProps = MakeScalarProps(5, requiredCount: 2);
        var nestedProp = MakeNestedClass("Address", MakeScalarProps(3));
        var allProps = scalarProps.Append(nestedProp).ToList();

        var parameters = new List<Parameter> { MakeOptionsClass("Opts", allProps) };
        var result = ParameterFlattener.Flatten(parameters);

        // All 5 scalar props flattened (nested path: flatten ALL scalars, not truncated)
        Assert.Equal(5, result.Parameters.Count);
        Assert.True(result.NeedsJsonInput);
    }

    [Fact]
    public void Flatten_OptionsClassWithZeroScalarProps_OnlyJsonInput()
    {
        var nestedProp = MakeNestedClass("Address", MakeScalarProps(3));
        var parameters = new List<Parameter> { MakeOptionsClass("Opts", new[] { nestedProp }) };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Empty(result.Parameters);
        Assert.True(result.NeedsJsonInput);
    }

    // -----------------------------------------------------------
    // CB301 diagnostic
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_AllRequiredBeyondThreshold_EmitsCB301()
    {
        // 12 required scalar props → first 10 flat, CB301 for props 11-12
        var props = MakeScalarProps(12, requiredCount: 12);
        var parameters = new List<Parameter> { MakeOptionsClass("Opts", props) };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Equal(10, result.Parameters.Count);
        Assert.True(result.NeedsJsonInput);
        // Two required params are hidden behind --json-input
        var cb301 = result.Diagnostics.Where(d => d.Code == "CB301").ToList();
        Assert.Equal(2, cb301.Count);
    }

    [Fact]
    public void Flatten_OptionalBeyondThreshold_NoCB301()
    {
        // 15 props, 3 required → all required fit in first 10, no CB301
        var props = MakeScalarProps(15, requiredCount: 3);
        var parameters = new List<Parameter> { MakeOptionsClass("Opts", props) };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CB301");
    }

    // -----------------------------------------------------------
    // Sort order: required first, then alphabetical
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_SortOrder_RequiredFirst_ThenAlphabetical()
    {
        var props = new List<Parameter>
        {
            MakePrimitive("Zebra", "string", required: false, nullable: true),
            MakePrimitive("Alpha", "string", required: false, nullable: true),
            MakePrimitive("Required2", "string", required: true),
            MakePrimitive("Required1", "string", required: true),
        };
        var parameters = new List<Parameter> { MakeOptionsClass("Opts", props) };
        var result = ParameterFlattener.Flatten(parameters);

        var names = result.Parameters.Select(p => p.PropertyName).ToList();
        // Required first (alphabetical), then optional (alphabetical)
        Assert.Equal(new[] { "Required1", "Required2", "Alpha", "Zebra" }, names);
    }

    // -----------------------------------------------------------
    // Multiple options classes on same operation
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_TwoOptionsClasses_CombinedFlattening()
    {
        var props1 = MakeScalarProps(3, requiredCount: 1);
        var props2 = new List<Parameter>
        {
            MakePrimitive("IdempotencyKey", "string", required: false, nullable: true),
            MakePrimitive("Timeout", "string", required: false, nullable: true),
        };

        var parameters = new List<Parameter>
        {
            MakeOptionsClass("CreateOpts", props1),
            MakeOptionsClass("RequestOpts", props2),
        };
        var result = ParameterFlattener.Flatten(parameters);

        // 3 + 2 = 5 flat params, all within threshold
        Assert.Equal(5, result.Parameters.Count);
        Assert.False(result.NeedsJsonInput);
    }

    // -----------------------------------------------------------
    // Enum parameters
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_EnumParam_PreservesEnumValues()
    {
        var parameters = new List<Parameter>
        {
            MakeEnum("Status", "OrderStatus", new[] { "Pending", "Shipped", "Delivered" })
        };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Single(result.Parameters);
        Assert.NotNull(result.Parameters[0].EnumValues);
        Assert.Equal(3, result.Parameters[0].EnumValues!.Count);
    }

    // -----------------------------------------------------------
    // Custom threshold
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_CustomThreshold_Respected()
    {
        var props = MakeScalarProps(6, requiredCount: 2);
        var parameters = new List<Parameter> { MakeOptionsClass("Opts", props) };
        var result = ParameterFlattener.Flatten(parameters, threshold: 4);

        Assert.Equal(4, result.Parameters.Count);
        Assert.True(result.NeedsJsonInput);
    }

    // -----------------------------------------------------------
    // Deduplication
    // -----------------------------------------------------------

    [Fact]
    public void Flatten_DuplicatePropertyNames_Deduplicated()
    {
        // Two options classes sharing "Position" and "Length" property names
        var props1 = new List<Parameter>
        {
            MakePrimitive("Name", "string", required: true),
            MakePrimitive("Position", "long", required: false, nullable: true),
            MakePrimitive("Length", "long", required: false, nullable: true),
        };
        var props2 = new List<Parameter>
        {
            MakePrimitive("Format", "string", required: false, nullable: true),
            MakePrimitive("Position", "long", required: false, nullable: true),
            MakePrimitive("Length", "long", required: false, nullable: true),
        };

        var parameters = new List<Parameter>
        {
            MakeOptionsClass("ImageOpts", props1),
            MakeOptionsClass("StreamOpts", props2),
        };
        var result = ParameterFlattener.Flatten(parameters);

        // 3 + 3 = 6 total, but 2 duplicates → 4 unique
        Assert.Equal(4, result.Parameters.Count);
        Assert.Single(result.Parameters.Where(p => p.CliFlag == "position"));
        Assert.Single(result.Parameters.Where(p => p.CliFlag == "length"));
    }

    [Fact]
    public void Flatten_DuplicateRequiredParam_EmitsCB303()
    {
        var props1 = new List<Parameter>
        {
            MakePrimitive("Id", "string", required: true),
        };
        var props2 = new List<Parameter>
        {
            MakePrimitive("Id", "string", required: true),
        };

        var parameters = new List<Parameter>
        {
            MakeOptionsClass("Opts1", props1),
            MakeOptionsClass("Opts2", props2),
        };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Single(result.Parameters);
        Assert.Contains(result.Diagnostics, d => d.Code == "CB303");
    }

    // -------------------------------------------------------
    // SourceOptionsClassName tracking (step 7A)
    // -------------------------------------------------------

    [Fact]
    public void OptionsClassParams_HaveSourceOptionsClassName()
    {
        var props = new List<Parameter>
        {
            MakePrimitive("Email", "string"),
            MakePrimitive("Name", "string", required: false, nullable: true),
        };
        var parameters = new List<Parameter>
        {
            new("options", new TypeRef(TypeKind.Class, "CreateCustomerOptions",
                Properties: props, Namespace: "TestNs"), true),
        };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.All(result.Parameters, fp =>
            Assert.Equal("CreateCustomerOptions", fp.SourceOptionsClassName));
    }

    [Fact]
    public void DirectParams_HaveNullSourceOptionsClassName()
    {
        var parameters = new List<Parameter>
        {
            MakePrimitive("id", "string"),
        };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Single(result.Parameters);
        Assert.Null(result.Parameters[0].SourceOptionsClassName);
    }

    [Fact]
    public void MultipleOptionsClasses_TrackSeparateClassNames()
    {
        var props1 = new List<Parameter> { MakePrimitive("Email", "string") };
        var props2 = new List<Parameter> { MakePrimitive("Key", "string", required: false, nullable: true) };
        var parameters = new List<Parameter>
        {
            new("options", new TypeRef(TypeKind.Class, "MainOptions", Properties: props1), true),
            new("extra", new TypeRef(TypeKind.Class, "ExtraOptions", Properties: props2), false),
        };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Equal(2, result.Parameters.Count);
        Assert.Equal("MainOptions", result.Parameters.First(p => p.CliFlag == "email").SourceOptionsClassName);
        Assert.Equal("ExtraOptions", result.Parameters.First(p => p.CliFlag == "key").SourceOptionsClassName);
    }

    // -------------------------------------------------------
    // SDK type info threading (step 7A)
    // -------------------------------------------------------

    [Fact]
    public void SdkTypeName_ThreadedThrough_ForPrimitives()
    {
        var parameters = new List<Parameter> { MakePrimitive("id", "string") };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Equal("string", result.Parameters[0].SdkTypeName);
        Assert.Equal(TypeKind.Primitive, result.Parameters[0].SdkTypeKind);
    }

    [Fact]
    public void SdkTypeName_ThreadedThrough_ForEnums()
    {
        var parameters = new List<Parameter>
        {
            MakeEnum("status", "CustomerStatus", ["Active", "Inactive"]),
        };
        var result = ParameterFlattener.Flatten(parameters);

        Assert.Equal("CustomerStatus", result.Parameters[0].SdkTypeName);
        Assert.Equal(TypeKind.Enum, result.Parameters[0].SdkTypeKind);
    }

    // -------------------------------------------------------
    // ComputeConversion (step 7A)
    // -------------------------------------------------------

    [Fact]
    public void ComputeConversion_String_ReturnsNull()
    {
        var type = new TypeRef(TypeKind.Primitive, "string");
        Assert.Null(ParameterFlattener.ComputeConversion(type));
    }

    [Fact]
    public void ComputeConversion_Int_ReturnsNull()
    {
        var type = new TypeRef(TypeKind.Primitive, "int");
        Assert.Null(ParameterFlattener.ComputeConversion(type));
    }

    [Fact]
    public void ComputeConversion_Enum_ReturnsEnumParse()
    {
        var type = new TypeRef(TypeKind.Enum, "CustomerStatus", EnumValues: ["Active"]);
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("Enum.Parse<CustomerStatus>", expr);
    }

    [Fact]
    public void ComputeConversion_NullableEnum_IncludesNullCheck()
    {
        var type = new TypeRef(TypeKind.Enum, "CustomerStatus", IsNullable: true, EnumValues: ["Active"]);
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("is not null", expr);
        Assert.Contains("(CustomerStatus?)null", expr);
    }

    [Fact]
    public void ComputeConversion_TimeSpan_ReturnsParse()
    {
        var type = new TypeRef(TypeKind.Primitive, "TimeSpan");
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("TimeSpan.Parse", expr);
    }

    [Fact]
    public void ComputeConversion_NullableTimeSpan_IncludesNullCheck()
    {
        var type = new TypeRef(TypeKind.Primitive, "TimeSpan", IsNullable: true);
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("is not null", expr);
    }

    [Fact]
    public void ComputeConversion_Guid_ReturnsParse()
    {
        var type = new TypeRef(TypeKind.Primitive, "Guid");
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("Guid.Parse", expr);
    }

    [Fact]
    public void ComputeConversion_Bool_ReturnsNull()
    {
        var type = new TypeRef(TypeKind.Primitive, "bool");
        Assert.Null(ParameterFlattener.ComputeConversion(type));
    }

    [Fact]
    public void ComputeConversion_NullableGuid_IncludesNullCheck()
    {
        var type = new TypeRef(TypeKind.Primitive, "Guid", IsNullable: true);
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("is not null", expr);
        Assert.Contains("(Guid?)null", expr);
    }

    [Fact]
    public void ComputeConversion_NullableDateTime_ReturnsParse()
    {
        var type = new TypeRef(TypeKind.Primitive, "DateTime", IsNullable: true);
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("DateTime.Parse", expr);
        Assert.Contains("is not null", expr);
    }

    [Fact]
    public void ComputeConversion_DateTimeOffset_ReturnsParse()
    {
        var type = new TypeRef(TypeKind.Primitive, "DateTimeOffset");
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("DateTimeOffset.Parse", expr);
    }

    [Fact]
    public void ComputeConversion_NullableDateTimeOffset_IncludesNullCheck()
    {
        var type = new TypeRef(TypeKind.Primitive, "DateTimeOffset", IsNullable: true);
        var expr = ParameterFlattener.ComputeConversion(type);
        Assert.NotNull(expr);
        Assert.Contains("is not null", expr);
    }

    [Fact]
    public void ComputeConversion_Class_ReturnsNull()
    {
        var type = new TypeRef(TypeKind.Class, "SomeClass");
        Assert.Null(ParameterFlattener.ComputeConversion(type));
    }

    [Fact]
    public void ComputeConversion_Array_ReturnsNull()
    {
        var type = new TypeRef(TypeKind.Array, "string[]");
        Assert.Null(ParameterFlattener.ComputeConversion(type));
    }

    [Fact]
    public void ComputeConversion_Dictionary_ReturnsNull()
    {
        var type = new TypeRef(TypeKind.Dictionary, "Dictionary");
        Assert.Null(ParameterFlattener.ComputeConversion(type));
    }
}
