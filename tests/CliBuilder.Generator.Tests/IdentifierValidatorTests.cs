using CliBuilder.Generator.CSharp;

namespace CliBuilder.Generator.Tests;

public class IdentifierValidatorTests
{
    // -----------------------------------------------------------
    // PascalToKebab
    // -----------------------------------------------------------

    [Theory]
    [InlineData("Customer", "customer")]
    [InlineData("PaymentIntent", "payment-intent")]
    [InlineData("GetAsync", "get-async")]
    [InlineData("ID", "id")]
    [InlineData("A", "a")]
    [InlineData("", "")]
    public void PascalToKebab_BasicCases(string input, string expected)
    {
        Assert.Equal(expected, IdentifierValidator.PascalToKebab(input));
    }

    [Theory]
    [InlineData("GetAPIKey", "get-api-key")]
    [InlineData("HTTPSClient", "https-client")]
    [InlineData("myHTTPSClient", "my-https-client")]
    [InlineData("XMLParser", "xml-parser")]
    [InlineData("IOStream", "io-stream")]
    public void PascalToKebab_AcronymHandling(string input, string expected)
    {
        Assert.Equal(expected, IdentifierValidator.PascalToKebab(input));
    }

    // -----------------------------------------------------------
    // KebabToPascal
    // -----------------------------------------------------------

    [Theory]
    [InlineData("customer", "Customer")]
    [InlineData("payment-intent", "PaymentIntent")]
    [InlineData("get-api-key", "GetApiKey")]
    [InlineData("", "")]
    public void KebabToPascal_BasicCases(string input, string expected)
    {
        Assert.Equal(expected, IdentifierValidator.KebabToPascal(input));
    }

    // -----------------------------------------------------------
    // IsKeyword — case insensitive
    // -----------------------------------------------------------

    [Theory]
    [InlineData("class", true)]
    [InlineData("Class", true)]
    [InlineData("CLASS", true)]
    [InlineData("int", true)]
    [InlineData("Int", true)]
    [InlineData("required", true)]  // C# 11
    [InlineData("record", true)]    // C# 9
    [InlineData("nint", true)]      // C# 9
    [InlineData("customer", false)]
    [InlineData("MyClass", false)]
    public void IsKeyword_CaseInsensitive(string name, bool expected)
    {
        Assert.Equal(expected, IdentifierValidator.IsKeyword(name));
    }

    // -----------------------------------------------------------
    // SanitizeParameter — keyword detection
    // -----------------------------------------------------------

    [Fact]
    public void SanitizeParameter_LowercaseKeyword_ReturnsVerbatimPrefix()
    {
        var (csharp, cli, diag) = IdentifierValidator.SanitizeParameter("class");
        Assert.Equal("@class", csharp);
        Assert.Equal("class-value", cli);
        Assert.NotNull(diag);
        Assert.Equal("CB004", diag!.Code);
    }

    [Fact]
    public void SanitizeParameter_PascalCaseKeyword_ReturnsVerbatimPrefix()
    {
        var (csharp, cli, diag) = IdentifierValidator.SanitizeParameter("Int");
        Assert.Equal("@Int", csharp);
        Assert.Contains("-value", cli);
        Assert.NotNull(diag);
        Assert.Equal("CB004", diag!.Code);
    }

    [Fact]
    public void SanitizeParameter_CSharp11Keyword_Detected()
    {
        var (csharp, _, diag) = IdentifierValidator.SanitizeParameter("required");
        Assert.Equal("@required", csharp);
        Assert.NotNull(diag);
    }

    // -----------------------------------------------------------
    // SanitizeParameter — boilerplate collision
    // -----------------------------------------------------------

    [Theory]
    [InlineData("Program")]
    [InlineData("JsonFormatter")]
    [InlineData("AuthHandler")]
    public void SanitizeParameter_BoilerplateName_EmitsDiagnostic(string name)
    {
        var (_, cli, diag) = IdentifierValidator.SanitizeParameter(name);
        Assert.Contains("-value", cli);
        Assert.NotNull(diag);
        Assert.Equal("CB004", diag!.Code);
    }

    [Fact]
    public void SanitizeParameter_NormalName_NoDiagnostic()
    {
        var (csharp, cli, diag) = IdentifierValidator.SanitizeParameter("Email");
        Assert.Equal("Email", csharp);
        Assert.Equal("email", cli);
        Assert.Null(diag);
    }

    // -----------------------------------------------------------
    // IsPathSafe
    // -----------------------------------------------------------

    [Theory]
    [InlineData("Customer", true)]
    [InlineData("MyClass", true)]
    [InlineData("../etc", false)]
    [InlineData("foo/bar", false)]
    [InlineData("foo\\bar", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    public void IsPathSafe_BasicCases(string name, bool expected)
    {
        Assert.Equal(expected, IdentifierValidator.IsPathSafe(name));
    }

    [Fact]
    public void IsPathSafe_NullByte_ReturnsFalse()
    {
        Assert.False(IdentifierValidator.IsPathSafe("foo\0bar"));
    }

    [Fact]
    public void IsPathSafe_ExceedsLengthLimit_ReturnsFalse()
    {
        Assert.False(IdentifierValidator.IsPathSafe(new string('A', 201)));
    }

    [Fact]
    public void IsPathSafe_ExactlyAtLengthLimit_ReturnsTrue()
    {
        Assert.True(IdentifierValidator.IsPathSafe(new string('A', 200)));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("NUL")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    public void IsPathSafe_WindowsReservedNames_ReturnsFalse(string name)
    {
        Assert.False(IdentifierValidator.IsPathSafe(name));
    }
}
