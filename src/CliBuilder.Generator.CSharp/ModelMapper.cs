using System.Text.Json;
using System.Text.RegularExpressions;
using CliBuilder.Core.Models;

namespace CliBuilder.Generator.CSharp;

public static partial class ModelMapper
{
    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex NumericLiteralRegex();

    public static (GeneratorModel Model, IReadOnlyList<Diagnostic> Diagnostics) Build(
        SdkMetadata metadata, GeneratorOptions options)
    {
        var diagnostics = new List<Diagnostic>();
        var cliName = options.CliName ?? DeriveCliName(metadata.Name);

        var resources = metadata.Resources.Select(r =>
            MapResource(r, diagnostics)).ToList();

        var auth = metadata.AuthPatterns.Count > 0
            ? MapAuth(metadata.AuthPatterns[0])
            : null;

        // Sanitize fields that flow into XML (csproj.sbn) to prevent MSBuild injection
        var sdkName = SanitizeXmlValue(metadata.Name);
        var sdkVersion = SanitizeXmlValue(metadata.Version);
        var sdkPackageName = SanitizeXmlValue(metadata.Name);

        // SdkProjectPath flows into csproj XML — sanitize if present
        var sdkProjectPath = options.SdkProjectPath != null
            ? SanitizeXmlValue(options.SdkProjectPath)
            : null;

        var model = new GeneratorModel(
            CliName: cliName,
            SdkName: sdkName,
            SdkVersion: sdkVersion,
            SdkPackageName: sdkPackageName,
            RootNamespace: DeriveNamespace(cliName),
            CliDescription: SanitizeString($"{cliName} — CLI for {sdkName}") ?? "",
            Resources: resources,
            Auth: auth,
            SdkProjectPath: sdkProjectPath);

        return (model, diagnostics);
    }

    private static ResourceModel MapResource(Resource resource, List<Diagnostic> diagnostics)
    {
        var className = IdentifierValidator.KebabToPascal(resource.Name);

        // Path safety check
        if (!IdentifierValidator.IsPathSafe(className))
        {
            var safeName = SanitizeToSafeName(resource.Name);
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "CB204",
                $"Resource name '{resource.Name}' is not path-safe — sanitized to '{safeName}'"));
            className = safeName;
        }

        // Keyword check on the PascalCase class name
        if (IdentifierValidator.IsKeyword(className.ToLowerInvariant()))
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "CB004",
                $"Resource '{resource.Name}' maps to C# keyword '{className}' — will use '@{className}' in generated code"));
        }

        var description = SanitizeString(resource.Description);

        var operations = resource.Operations.Select(op =>
            MapOperation(op, diagnostics)).ToList();

        return new ResourceModel(resource.Name, className, description, operations,
            SourceClassName: SanitizeString(resource.SourceClassName),
            SourceNamespace: SanitizeString(resource.SourceNamespace));
    }

    private static OperationModel MapOperation(Operation operation, List<Diagnostic> diagnostics)
    {
        var methodName = IdentifierValidator.KebabToPascal(operation.Name);
        var description = SanitizeString(operation.Description);
        var returnTypeName = MapTypeName(operation.ReturnType);

        var flattenResult = ParameterFlattener.Flatten(operation.Parameters);
        diagnostics.AddRange(flattenResult.Diagnostics);

        // Find the options class name (first class-typed parameter) for handler wiring.
        // Operations with multiple class-typed params (e.g., options + requestContext)
        // use only the first — the second is accessible via --json-input.
        var optionsParam = operation.Parameters
            .FirstOrDefault(p => p.Type.Kind == TypeKind.Class && p.Type.Properties != null);

        return new OperationModel(
            Name: operation.Name,
            MethodName: methodName,
            Description: description,
            Parameters: flattenResult.Parameters,
            NeedsJsonInput: flattenResult.NeedsJsonInput,
            ReturnTypeName: returnTypeName,
            IsStreaming: operation.IsStreaming,
            SourceMethodName: SanitizeString(operation.SourceMethodName),
            OptionsClassName: SanitizeString(optionsParam?.Type.Name));
    }

    private static AuthModel MapAuth(AuthPattern pattern) =>
        new(Type: pattern.Type.ToString(),
            EnvVar: SanitizeString(pattern.EnvVar) ?? pattern.EnvVar,
            ParameterName: SanitizeString(pattern.ParameterName) ?? pattern.ParameterName);

    // -----------------------------------------------------------
    // Sanitization
    // -----------------------------------------------------------

    /// <summary>
    /// Neutralize Scriban template syntax and C# metacharacters in description strings.
    /// Primary sanitization barrier — runs before strings reach the template engine.
    /// </summary>
    internal static string? SanitizeString(string? value)
    {
        if (value is null) return null;

        // Neutralize Scriban template syntax
        value = value.Replace("{{", "{<").Replace("}}", ">}");
        value = value.Replace("{%", "{<").Replace("%}", ">}");

        return value;
    }

    /// <summary>
    /// Type-whitelist DefaultValue — only emit literal values for known primitive types.
    /// Returns a C# literal string or null.
    /// </summary>
    internal static string? SanitizeDefaultValue(
        JsonElement? defaultValue, TypeRef type, List<Diagnostic> diagnostics)
    {
        if (defaultValue is null || defaultValue.Value.ValueKind == JsonValueKind.Null)
            return null;

        var val = defaultValue.Value;
        return val.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number when type.Name is "int" or "long" or "short" or "byte"
                => ValidateNumericLiteral(val.GetRawText(), diagnostics),
            JsonValueKind.Number when type.Name is "decimal"
                => ValidateNumericLiteral(val.GetRawText(), diagnostics) is { } n ? $"{n}m" : null,
            JsonValueKind.Number when type.Name is "double"
                => ValidateNumericLiteral(val.GetRawText(), diagnostics) is { } n ? $"{n}d" : null,
            JsonValueKind.Number when type.Name is "float"
                => ValidateNumericLiteral(val.GetRawText(), diagnostics) is { } n ? $"{n}f" : null,
            JsonValueKind.Number => ValidateNumericLiteral(val.GetRawText(), diagnostics),
            JsonValueKind.String => $"@\"{EscapeVerbatimString(val.GetString()!)}\"",
            _ => RejectComplexDefault(val, diagnostics)
        };
    }

    private static string? RejectComplexDefault(JsonElement val, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "CB302",
            $"Default value of kind '{val.ValueKind}' cannot be safely emitted — ignored"));
        return null;
    }

    // -----------------------------------------------------------
    // Type mapping
    // -----------------------------------------------------------

    /// <summary>
    /// Map a TypeRef to a C# type name.
    /// When forCliParam is true, complex types (Class, Generic, Array) map to "string"
    /// because CLI options can only accept primitive types — complex values come via --json-input.
    /// When forCliParam is false (return types, comments), the original SDK type name is preserved.
    /// </summary>
    internal static string MapTypeName(TypeRef type, bool forCliParam = false)
    {
        var baseName = type.Kind switch
        {
            TypeKind.Primitive => MapPrimitiveType(type.Name),
            TypeKind.Enum => "string",
            TypeKind.Class when forCliParam => "string",     // CLI: accept as JSON string
            TypeKind.Class => type.Name,                      // return types: preserve SDK name
            TypeKind.Array when forCliParam => "string",
            TypeKind.Array => type.ElementType != null ? $"{MapTypeName(type.ElementType, forCliParam)}[]" : "object[]",
            TypeKind.Dictionary => "string",
            TypeKind.Generic when forCliParam => "string",
            TypeKind.Generic => type.GenericArguments?.Count > 0
                ? $"{type.Name}<{string.Join(", ", type.GenericArguments.Select(t => MapTypeName(t)))}>"
                : type.Name,
            _ => "object"
        };

        // Append ? for nullable value types (string is already nullable by reference)
        if (type.IsNullable && baseName is not "string" and not "object")
            return $"{baseName}?";

        return baseName;
    }

    private static string MapPrimitiveType(string name) => name switch
    {
        "string" => "string",
        "int" or "Int32" => "int",
        "long" or "Int64" => "long",
        "bool" or "Boolean" => "bool",
        "double" or "Double" => "double",
        "float" or "Single" => "float",
        "decimal" or "Decimal" => "decimal",
        "byte" or "Byte" => "byte",
        "short" or "Int16" => "short",
        // Non-primitive structs that appear as "primitive" in reflection
        "TimeSpan" => "string",   // CLI: accept as string, parse in handler
        "DateTime" => "string",
        "DateTimeOffset" => "string",
        "Guid" => "string",
        "void" or "Void" => "void",
        _ => "string"  // safe default for unknown types
    };

    // -----------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------

    internal static string DeriveCliName(string sdkName)
    {
        // "CliBuilder.TestSdk" → "clibuilder-testsdk"
        // "OpenAI" → "openai"
        var name = sdkName
            .Replace(".", "-")
            .Replace(" ", "-")
            .ToLowerInvariant();

        // Remove consecutive hyphens
        while (name.Contains("--"))
            name = name.Replace("--", "-");

        return name.Trim('-');
    }

    internal static string DeriveNamespace(string cliName)
    {
        // "testsdk-cli" → "TestsdkCli"
        return IdentifierValidator.KebabToPascal(cliName);
    }

    /// <summary>
    /// Sanitize a string for safe inclusion in XML attributes (csproj).
    /// Strips characters that could break out of XML structure.
    /// </summary>
    internal static string SanitizeXmlValue(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    /// <summary>
    /// Validate that a GetRawText() result is a safe numeric literal.
    /// Defense-in-depth: System.Text.Json constrains this, but we verify.
    /// </summary>
    private static string? ValidateNumericLiteral(string rawText, List<Diagnostic> diagnostics)
    {
        if (NumericLiteralRegex().IsMatch(rawText))
            return rawText;

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "CB302",
            $"Numeric default value '{rawText}' failed format validation — ignored"));
        return null;
    }

    internal static string EscapeVerbatimString(string value) =>
        value.Replace("\"", "\"\"");

    private static string SanitizeToSafeName(string name)
    {
        // Strip path-unsafe characters, derive a PascalCase name
        var safe = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                safe.Append(c);
        }
        var safeName = safe.Length > 0 ? safe.ToString() : "Unknown";
        return IdentifierValidator.KebabToPascal(safeName);
    }
}
