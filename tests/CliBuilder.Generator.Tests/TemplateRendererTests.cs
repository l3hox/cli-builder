using CliBuilder.Generator.CSharp;

namespace CliBuilder.Generator.Tests;

public class TemplateRendererTests
{
    private readonly TemplateRenderer _renderer = new();

    // -----------------------------------------------------------
    // to_var_name custom function
    // -----------------------------------------------------------

    [Theory]
    [InlineData("email", "email")]
    [InlineData("class-value", "classValue")]
    [InlineData("credit-limit", "creditLimit")]
    [InlineData("json-input", "jsonInput")]
    [InlineData("id", "id")]
    [InlineData("a-b", "aB")]
    [InlineData("a-b-c", "aBC")]
    [InlineData("", "_param")]
    public void ToVarName_ProducesExpected(string input, string expected)
    {
        var result = _renderer.RenderInline("{{ value | to_var_name }}", new { Value = input });
        Assert.Equal(expected, result.Trim());
    }

    // -----------------------------------------------------------
    // escape_csharp custom function
    // -----------------------------------------------------------

    [Theory]
    [InlineData("hello", "@\"hello\"")]
    [InlineData("say \"hi\"", "@\"say \"\"hi\"\"\"")]
    public void EscapeCSharp_ProducesVerbatimLiteral(string input, string expected)
    {
        var result = _renderer.RenderInline("{{ value | escape_csharp }}", new { Value = input });
        Assert.Equal(expected, result.Trim());
    }

    [Fact]
    public void EscapeCSharp_Null_ProducesNullLiteral()
    {
        var result = _renderer.RenderInline("{{ value | escape_csharp }}", new { Value = (string?)null });
        Assert.Equal("null", result.Trim());
    }

    // -----------------------------------------------------------
    // apply_conversion custom function (step 7B)
    // -----------------------------------------------------------

    [Fact]
    public void ApplyConversion_NullExpression_ReturnsVarNameValue()
    {
        Assert.Equal("emailValue", TemplateRenderer.ApplyConversion("email", null));
    }

    [Fact]
    public void ApplyConversion_EnumExpression_SubstitutesPlaceholder()
    {
        var result = TemplateRenderer.ApplyConversion("status", "Enum.Parse<CustomerStatus>({0})");
        Assert.Equal("Enum.Parse<CustomerStatus>(statusValue)", result);
    }

    [Fact]
    public void ApplyConversion_NullableTimeSpan_SubstitutesBothOccurrences()
    {
        var expr = "{0} is not null ? TimeSpan.Parse({0}) : (TimeSpan?)null";
        var result = TemplateRenderer.ApplyConversion("timeout", expr);
        Assert.Equal("timeoutValue is not null ? TimeSpan.Parse(timeoutValue) : (TimeSpan?)null", result);
    }

    [Fact]
    public void ApplyConversion_ViaTemplate_IdentityPath()
    {
        var result = _renderer.RenderInline(
            "{{ name | apply_conversion expr }}", new { Name = "email", Expr = (string?)null });
        Assert.Equal("emailValue", result.Trim());
    }

    [Fact]
    public void ApplyConversion_ViaTemplate_ConversionPath()
    {
        var result = _renderer.RenderInline(
            "{{ name | apply_conversion expr }}", new { Name = "status", Expr = "Enum.Parse<X>({0})" });
        Assert.Equal("Enum.Parse<X>(statusValue)", result.Trim());
    }
}
