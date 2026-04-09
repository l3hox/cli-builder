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
            MapResource(r, diagnostics, metadata.StaticAuthSetup)).ToList();

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
            SdkProjectPath: sdkProjectPath,
            StaticAuthSetup: SanitizeString(metadata.StaticAuthSetup));

        return (model, diagnostics);
    }

    private static ResourceModel MapResource(Resource resource, List<Diagnostic> diagnostics, string? staticAuthSetup = null)
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

        // Build constructor info from ConstructorParams
        var (ctorExpr, ctorConfigParams, canConstruct) = BuildConstructorInfo(resource, diagnostics, staticAuthSetup);

        // Collect all namespaces needed by this resource's generated code
        var namespaces = new HashSet<string>();
        if (resource.SourceNamespace != null)
            namespaces.Add(resource.SourceNamespace);
        // Add namespaces from constructor params (e.g., ApiKeyCredential namespace)
        if (resource.ConstructorParams != null)
        {
            foreach (var cp in resource.ConstructorParams.Where(p => p.IsAuth && p.TypeNamespace != null))
                namespaces.Add(cp.TypeNamespace!);
        }
        foreach (var op in operations)
        {
            if (op.MethodParams != null)
            {
                foreach (var mp in op.MethodParams)
                {
                    if (mp.Namespace != null)
                        namespaces.Add(mp.Namespace);
                }
            }
        }
        // Collect generic argument namespaces from original parameters
        // (e.g., List<ChatMessage> needs using OpenAI.Chat)
        foreach (var srcOp in resource.Operations)
        {
            foreach (var p in srcOp.Parameters)
                CollectGenericArgumentNamespaces(p.Type, namespaces);
        }
        var requiredNamespaces = namespaces
            .Where(ns => IdentifierValidator.IsValidNamespace(ns))
            .Distinct()
            .OrderBy(ns => ns)
            .ToList();

        return new ResourceModel(resource.Name, className, description, operations,
            SourceClassName: SanitizeString(resource.SourceClassName),
            SourceNamespace: SanitizeString(resource.SourceNamespace),
            ConstructorExpression: ctorExpr,
            RequiredNamespaces: requiredNamespaces,
            CanConstruct: canConstruct,
            ConstructorConfigParams: ctorConfigParams);
    }

    private static (string? Expression, IReadOnlyList<ConstructorConfigParam> ConfigParams, bool CanConstruct)
        BuildConstructorInfo(Resource resource, List<Diagnostic> diagnostics, string? staticAuthSetup = null)
    {
        if (resource.ConstructorParams is null || resource.ConstructorParams.Count == 0)
        {
            // No auth constructor found. If the SDK has static auth (e.g., StripeConfiguration.ApiKey)
            // and the service has a parameterless constructor, it can be constructed.
            if (staticAuthSetup != null && resource.HasParameterlessCtor)
                return ("", Array.Empty<ConstructorConfigParam>(), true);
            return (null, Array.Empty<ConstructorConfigParam>(), false);
        }

        var configParams = new List<ConstructorConfigParam>();
        var argParts = new List<string>();
        var hasAuth = false;

        foreach (var p in resource.ConstructorParams)
        {
            if (p.IsAuth)
            {
                hasAuth = true;
                if (p.TypeName == "string")
                {
                    argParts.Add("credential");
                }
                else if (IdentifierValidator.IsValidIdentifier(p.TypeName))
                {
                    argParts.Add($"new {p.TypeName}(credential)");
                }
                else
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "CB205",
                        $"Constructor auth type '{p.TypeName}' is not a valid C# identifier — falling back to raw credential"));
                    argParts.Add("credential");
                }
            }
            else if (p.IsRequired)
            {
                // Non-auth required param → becomes a CLI option (e.g., --model)
                var (_, cliFlag, _) = IdentifierValidator.SanitizeParameter(p.Name);
                var varName = KebabToCamelCase(cliFlag) + "Value";
                // Flow the type from ConstructorParam — most are string, but preserve the actual type
                var csharpType = MapPrimitiveType(p.TypeName);
                configParams.Add(new ConstructorConfigParam(cliFlag, varName, csharpType, true));
                argParts.Add(varName);
            }
            // Optional non-auth params: omitted from constructor call for v1
        }

        // Must have at least one auth param to construct the client
        if (!hasAuth)
            return (null, Array.Empty<ConstructorConfigParam>(), false);

        return (string.Join(", ", argParts), configParams, true);
    }

    private static OperationModel MapOperation(Operation operation, List<Diagnostic> diagnostics)
    {
        var methodName = IdentifierValidator.KebabToPascal(operation.Name);
        var description = SanitizeString(operation.Description);
        var returnTypeName = MapTypeName(operation.ReturnType);

        var flattenResult = ParameterFlattener.Flatten(operation.Parameters);
        diagnostics.AddRange(flattenResult.Diagnostics);

        // When --json-input is present, all value-type CLI options must be nullable
        // so "user didn't provide" (null) is distinguishable from "user set the default"
        // (false/0). Without this, System.CommandLine defaults clobber JSON values.
        var parameters = flattenResult.NeedsJsonInput
            ? MakeValueTypesNullable(flattenResult.Parameters)
            : flattenResult.Parameters;

        // Find the options class name (first class-typed parameter) for handler wiring.
        // Operations with multiple class-typed params (e.g., options + requestContext)
        // use only the first — the second is accessible via --json-input.
        var optionsParam = operation.Parameters
            .FirstOrDefault(p => p.Type.Kind == TypeKind.Class && p.Type.Properties != null);

        // Build ordered method parameter models for SDK call reconstruction
        var methodParams = BuildMethodParams(operation.Parameters);

        // Check if all direct (non-options-class) params can be converted from CLI types.
        // Also check that the return type is awaitable (not a raw class like AsyncCollectionResult).
        var canWire = CanWireOperation(operation, methodParams, diagnostics);

        var hasJsonDirectParams = methodParams.Any(mp => mp.NeedsJsonDeserialization);
        var needsJsonInput = flattenResult.NeedsJsonInput || hasJsonDirectParams;

        return new OperationModel(
            Name: operation.Name,
            MethodName: methodName,
            Description: description,
            Parameters: parameters,
            NeedsJsonInput: needsJsonInput,
            ReturnTypeName: returnTypeName,
            IsStreaming: operation.IsStreaming,
            SourceMethodName: SanitizeString(operation.SourceMethodName),
            OptionsClassName: SanitizeString(optionsParam?.Type.Name),
            MethodParams: methodParams,
            CanWireSdkCall: canWire,
            HasJsonDirectParams: hasJsonDirectParams);
    }

    private static bool CanWireOperation(Operation operation,
        IReadOnlyList<MethodParamModel> methodParams, List<Diagnostic> diagnostics)
    {
        // Check direct params: non-options-class params must be convertible from CLI string
        for (int i = 0; i < operation.Parameters.Count; i++)
        {
            var p = operation.Parameters[i];
            if (p.Type.Kind == TypeKind.Class && p.Type.Properties != null)
                continue; // options class — handled by construction

            // Complex direct params: Generic, Array, Dictionary, bare Class
            if (p.Type.Kind is TypeKind.Generic or TypeKind.Array or TypeKind.Dictionary
                || (p.Type.Kind == TypeKind.Class && p.Type.Properties == null))
            {
                // Binary types (BinaryContent, Stream, etc.) — can't deserialize from JSON
                if (IsBinaryType(p.Type.Name))
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "CB306",
                        $"Operation '{operation.Name}' has binary parameter " +
                        $"'{p.Name}' ({p.Type.Name}) — falling back to echo stub"));
                    return false;
                }

                // Infrastructure types (RequestOptions, etc.) — SDK plumbing, not user-facing
                if (IsInfrastructureType(p.Type.Name))
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "CB306",
                        $"Operation '{operation.Name}' has infrastructure parameter " +
                        $"'{p.Name}' ({p.Type.Name}) — falling back to echo stub"));
                    return false;
                }

                // JSON-deserializable — allow, will use --json-input
                // Check for abstract inner types (CB307 info diagnostic, not a blocker)
                if (p.Type.GenericArguments?.Any(ga => ga.IsAbstract) == true
                    || (p.Type.Kind == TypeKind.Class && p.Type.IsAbstract))
                {
                    var innerName = p.Type.GenericArguments?.FirstOrDefault(ga => ga.IsAbstract)?.Name ?? p.Type.Name;
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "CB307",
                        $"Operation '{operation.Name}' has abstract parameter '{p.Name}' ({innerName}) " +
                        "— deserialization requires SDK-registered JsonConverters"));
                }
                continue;
            }
        }

        // Check return type: known non-awaitable types that slip through unwrapping.
        // Normal class return types (Customer, Order) are fine — they came from Task<T> unwrapping.
        // But non-generic collection types and sub-client factory return types are not awaitable.
        // The suffix list matches the adapter's service class suffixes (Service, Client, Api)
        // plus Settings types, and the known collection wrappers.
        if (operation.ReturnType.Kind == TypeKind.Class && !operation.IsStreaming)
        {
            var name = operation.ReturnType.Name;
            // Known non-awaitable patterns: collection wrappers, sub-client factories,
            // settings/options types, generic type params (T), response/notification types
            if (name is "AsyncCollectionResult" or "CollectionResult" or "Uri" or "Stream"
                || name.Length == 1  // generic type parameter like "T"
                || name.EndsWith("Client") || name.EndsWith("Service") || name.EndsWith("Api")
                || name.EndsWith("ClientSettings") || name.EndsWith("Options")
                || name.EndsWith("Response") || name.EndsWith("Notification"))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "CB306",
                    $"Operation '{operation.Name}' returns non-awaitable type '{name}' — falling back to echo stub"));
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal)
    {
        "bool", "int", "long", "short", "byte", "float", "double", "decimal"
    };

    private static IReadOnlyList<FlatParameter> MakeValueTypesNullable(IReadOnlyList<FlatParameter> parameters)
    {
        return parameters.Select(p =>
        {
            // Only make options class params nullable — direct method params
            // aren't affected by --json-input and must keep their original types.
            if (ValueTypes.Contains(p.CSharpType) && p.SourceOptionsClassName != null)
            {
                var conversion = p.ConversionExpression ?? "{0}.Value";
                return p with { CSharpType = p.CSharpType + "?", ConversionExpression = conversion };
            }
            return p;
        }).ToList();
    }

    private static IReadOnlyList<MethodParamModel> BuildMethodParams(IReadOnlyList<Parameter> parameters)
    {
        var methodParams = new List<MethodParamModel>();
        foreach (var p in parameters)
        {
            if (p.Type.Kind == TypeKind.Class && p.Type.Properties != null)
            {
                // Options class — constructed and populated from --json-input + flat flags
                var typeName = SanitizeString(p.Type.Name) ?? p.Type.Name;
                methodParams.Add(new MethodParamModel(
                    ArgExpression: PascalToCamelCase(typeName),
                    TypeName: typeName,
                    Namespace: SanitizeString(p.Type.Namespace),
                    IsOptionsClass: true));
            }
            else if (p.Type.Kind is TypeKind.Generic or TypeKind.Array or TypeKind.Dictionary
                     || (p.Type.Kind == TypeKind.Class && p.Type.Properties == null
                         && !IsBinaryType(p.Type.Name) && !IsInfrastructureType(p.Type.Name)))
            {
                // Complex direct param — deserialized from --json-input
                var (_, cliFlag, _) = IdentifierValidator.SanitizeParameter(p.Name);
                var deserTypeName = BuildDeserializationTypeName(p.Type);
                methodParams.Add(new MethodParamModel(
                    ArgExpression: KebabToCamelCase(cliFlag) + "Value",
                    TypeName: deserTypeName,
                    Namespace: SanitizeString(p.Type.Namespace),
                    IsOptionsClass: false,
                    NeedsJsonDeserialization: true,
                    DeserializationTypeName: deserTypeName,
                    JsonPropertyName: p.Name,
                    IsRequired: p.Required));
            }
            else
            {
                // Primitive/enum direct param — from CLI flag
                var (_, cliFlag, _) = IdentifierValidator.SanitizeParameter(p.Name);
                methodParams.Add(new MethodParamModel(
                    ArgExpression: KebabToCamelCase(cliFlag) + "Value",
                    TypeName: null,
                    Namespace: null,
                    IsOptionsClass: false));
            }
        }
        return methodParams;
    }

    private static readonly HashSet<string> BinaryTypeNames = new(StringComparer.Ordinal)
    {
        "BinaryContent", "BinaryData", "Stream", "ReadOnlyMemory", "ReadOnlySpan"
    };

    private static readonly HashSet<string> InfrastructureTypeNames = new(StringComparer.Ordinal)
    {
        "RequestOptions", "CancellationToken"
    };

    private static bool IsBinaryType(string name) => BinaryTypeNames.Contains(name);
    private static bool IsInfrastructureType(string name) =>
        InfrastructureTypeNames.Contains(name)
        || name.EndsWith("ClientOptions", StringComparison.Ordinal)
        || name.EndsWith("ClientSettings", StringComparison.Ordinal);

    private static void CollectGenericArgumentNamespaces(TypeRef type, HashSet<string> namespaces)
    {
        if (type.GenericArguments == null) return;
        foreach (var ga in type.GenericArguments)
        {
            if (ga.Namespace != null)
                namespaces.Add(ga.Namespace);
            CollectGenericArgumentNamespaces(ga, namespaces);
        }
    }

    internal static string BuildDeserializationTypeName(TypeRef type)
    {
        if (type.Kind == TypeKind.Array)
        {
            var elementName = type.ElementType?.Name ?? type.GenericArguments?.FirstOrDefault()?.Name ?? "object";
            return $"{elementName}[]";
        }

        if (type.Kind == TypeKind.Dictionary && type.GenericArguments is { Count: 2 })
        {
            var keyName = type.GenericArguments[0].Name;
            var valueName = type.GenericArguments[1].Name;
            return $"Dictionary<{keyName}, {valueName}>";
        }

        if (type.Kind == TypeKind.Generic && type.GenericArguments is { Count: > 0 })
        {
            // IDictionary<K,V> comes through as Generic with 2 args
            if (type.Name.Contains("Dictionary") && type.GenericArguments.Count == 2)
            {
                var keyName = type.GenericArguments[0].Name;
                var valueName = type.GenericArguments[1].Name;
                return $"Dictionary<{keyName}, {valueName}>";
            }
            var innerName = type.GenericArguments[0].Name;
            return $"List<{innerName}>";
        }

        // Bare class
        return type.Name;
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

    /// <summary>
    /// Convert PascalCase to camelCase (lowercase first character).
    /// "CreateCustomerOptions" → "createCustomerOptions"
    /// </summary>
    internal static string PascalToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    /// <summary>
    /// Convert kebab-case to camelCase. Delegates to the shared implementation
    /// in IdentifierValidator to stay in sync with TemplateRenderer.ToVarName.
    /// </summary>
    internal static string KebabToCamelCase(string value) =>
        IdentifierValidator.KebabToCamelCase(value);

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
