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
}
