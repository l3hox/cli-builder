using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CliBuilder.Core.Adapters;
using CliBuilder.Core.Models;

namespace CliBuilder.Adapter.DotNet;

public class DotNetAdapter : ISdkAdapter
{
    private const int MaxTypeRecursionDepth = 10;

    private static readonly string[] DefaultSuffixes = ["Service", "Client", "Api"];
    private static readonly string[] AsyncSuffixes = ["Async"];

    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        "Boolean", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32",
        "Int64", "UInt64", "Single", "Double", "Decimal", "String", "Char",
        "Guid", "DateTime", "DateTimeOffset", "TimeSpan"
    };

    private static readonly Dictionary<string, string> PrimitiveAliases = new(StringComparer.Ordinal)
    {
        ["Boolean"] = "bool", ["Byte"] = "byte", ["SByte"] = "sbyte",
        ["Int16"] = "short", ["UInt16"] = "ushort", ["Int32"] = "int",
        ["UInt32"] = "uint", ["Int64"] = "long", ["UInt64"] = "ulong",
        ["Single"] = "float", ["Double"] = "double", ["Decimal"] = "decimal",
        ["String"] = "string", ["Char"] = "char",
    };

    // Types that are unwrapped to their first generic argument
    private static readonly HashSet<string> UnwrapTypes = new(StringComparer.Ordinal)
    {
        "Task", "ValueTask", "ClientResult"
    };

    // Types that are unwrapped and mark the operation as streaming
    private static readonly HashSet<string> StreamingUnwrapTypes = new(StringComparer.Ordinal)
    {
        "IAsyncEnumerable"
    };

    public AdapterResult Extract(AdapterOptions options)
    {
        var diagnostics = new List<Diagnostic>();

        var assemblyPath = Path.GetFullPath(options.AssemblyPath);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Assembly not found: {assemblyPath}", assemblyPath);

        using var mlc = CreateMetadataLoadContext(assemblyPath);
        var assembly = mlc.LoadFromAssemblyPath(assemblyPath);

        var assemblyName = assembly.GetName();
        var sdkName = assemblyName.Name ?? Path.GetFileNameWithoutExtension(assemblyPath);
        var sdkVersion = assemblyName.Version?.ToString() ?? "0.0.0";

        // Discover service classes (with ReflectionTypeLoadException guard)
        var serviceClasses = DiscoverServiceClasses(assembly, diagnostics);

        // Build resources from discovered classes
        var resources = new List<Resource>();
        foreach (var (noun, type) in serviceClasses)
        {
            var operations = ExtractOperations(type, diagnostics);
            resources.Add(new Resource(noun, null, operations));
        }

        // Detect auth patterns
        var authPatterns = DetectAuthPatterns(assembly, serviceClasses);

        var metadata = new SdkMetadata(sdkName, sdkVersion, resources, authPatterns);
        return new AdapterResult(metadata, diagnostics);
    }

    private MetadataLoadContext CreateMetadataLoadContext(string assemblyPath)
    {
        var assemblyDir = Path.GetDirectoryName(assemblyPath)!;
        var paths = new List<string>();

        // 1. Sibling DLLs
        paths.AddRange(Directory.GetFiles(assemblyDir, "*.dll"));

        // 2. .NET runtime reference assemblies
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        if (Directory.Exists(runtimeDir))
            paths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));

        var resolver = new PathAssemblyResolver(paths);
        return new MetadataLoadContext(resolver);
    }

    private List<(string Noun, Type Type)> DiscoverServiceClasses(Assembly assembly, List<Diagnostic> diagnostics)
    {
        // Guard against missing transitive dependencies
        Type[] exportedTypes;
        try
        {
            exportedTypes = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Use whatever types did load; emit diagnostics for failures
            exportedTypes = ex.Types.Where(t => t != null).ToArray()!;
            foreach (var loaderEx in ex.LoaderExceptions.Where(e => e != null))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "CB101",
                    $"Type skipped due to load failure: {loaderEx!.Message}"));
            }
        }

        var candidates = new Dictionary<string, List<Type>>();

        foreach (var type in exportedTypes)
        {
            var noun = TryExtractNoun(type.Name);
            if (noun == null) continue;

            if (!candidates.TryGetValue(noun, out var list))
            {
                list = new List<Type>();
                candidates[noun] = list;
            }
            list.Add(type);
        }

        var result = new List<(string, Type)>();
        foreach (var (noun, types) in candidates)
        {
            if (types.Count > 1)
            {
                var classNames = string.Join(", ", types.Select(t => t.Name));
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "CB202",
                    $"Noun collision: classes {classNames} all map to resource '{noun}'. Add an override in cli-builder.json to disambiguate."));
                // Collision: skip all colliding classes — require config override
                continue;
            }

            result.Add((noun, types[0]));
        }

        return result;
    }

    private string? TryExtractNoun(string className)
    {
        foreach (var suffix in DefaultSuffixes)
        {
            if (className.EndsWith(suffix, StringComparison.Ordinal) && className.Length > suffix.Length)
            {
                var raw = className[..^suffix.Length];
                return PascalToKebabCase(raw);
            }
        }
        return null;
    }

    private IReadOnlyList<Operation> ExtractOperations(Type serviceType, List<Diagnostic> diagnostics)
    {
        var methods = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        // Group by verb name to detect collisions
        var verbGroups = new Dictionary<string, List<MethodInfo>>();
        foreach (var method in methods)
        {
            if (method.IsSpecialName) continue; // skip property accessors, etc.

            var verb = ExtractVerbName(method.Name);
            if (!verbGroups.TryGetValue(verb, out var list))
            {
                list = new List<MethodInfo>();
                verbGroups[verb] = list;
            }
            list.Add(method);
        }

        var operations = new List<Operation>();
        foreach (var (verb, group) in verbGroups)
        {
            // Check for non-overload collision (different method names → same verb)
            var distinctMethodNames = group.Select(m => m.Name).Distinct().ToList();
            if (distinctMethodNames.Count > 1)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "CB201",
                    $"Verb collision: methods {string.Join(", ", distinctMethodNames.Select(n => $"'{n}'"))} on {serviceType.Name} all map to verb '{verb}'. Add an override in cli-builder.json to disambiguate."));
            }

            // Handle overloads: pick the one with the most parameters (richest)
            MethodInfo selected;
            if (group.Count > 1)
            {
                // Among methods with the same name, pick richest parameter set
                var resolved = new List<MethodInfo>();
                foreach (var nameGroup in group.GroupBy(m => m.Name))
                {
                    var overloadList = nameGroup.ToList();
                    if (overloadList.Count > 1)
                    {
                        resolved.Add(SelectRichestOverload(overloadList, verb, serviceType, diagnostics));
                    }
                    else
                    {
                        resolved.Add(overloadList[0]);
                    }
                }

                // If we still have multiple (from different method names), pick the async one
                selected = resolved.FirstOrDefault(m => m.Name.EndsWith("Async")) ?? resolved[0];
            }
            else
            {
                selected = group[0];
            }

            var parameters = ExtractParameters(selected);
            var (returnType, isStreaming) = ExtractReturnType(selected);

            operations.Add(new Operation(verb, null, parameters, returnType, isStreaming));
        }

        return operations;
    }

    private MethodInfo SelectRichestOverload(List<MethodInfo> overloads, string verb, Type serviceType, List<Diagnostic> diagnostics)
    {
        var sorted = overloads.OrderByDescending(m =>
            m.GetParameters().Count(p => p.ParameterType.FullName != "System.Threading.CancellationToken")).ToList();

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Info,
            "CB203",
            $"Overload disambiguated: '{sorted[0].Name}' on {serviceType.Name} has {sorted.Count} overloads for verb '{verb}'. Using the variant with {sorted[0].GetParameters().Length} parameters."));

        return sorted[0];
    }

    private string ExtractVerbName(string methodName)
    {
        var name = methodName;
        foreach (var suffix in AsyncSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        return PascalToKebabCase(name);
    }

    private IReadOnlyList<Parameter> ExtractParameters(MethodInfo method)
    {
        var parameters = new List<Parameter>();
        foreach (var param in method.GetParameters())
        {
            // Skip CancellationToken
            if (param.ParameterType.FullName == "System.Threading.CancellationToken")
                continue;

            var typeRef = BuildTypeRef(param.ParameterType);

            // Check if the parameter type is a class with properties (options object)
            if (typeRef.Kind == TypeKind.Class && !IsPrimitiveType(param.ParameterType))
            {
                var properties = ExtractClassProperties(param.ParameterType, depth: 0);
                typeRef = typeRef with { Properties = properties };
            }

            var isNullable = IsNullableParameter(param);
            if (isNullable && !typeRef.IsNullable)
                typeRef = typeRef with { IsNullable = true };

            parameters.Add(new Parameter(
                param.Name ?? "unknown",
                typeRef,
                !param.HasDefaultValue && !isNullable
            ));
        }
        return parameters;
    }

    private IReadOnlyList<Parameter> ExtractClassProperties(Type type, int depth = 0)
    {
        var props = new List<Parameter>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var typeRef = BuildTypeRef(prop.PropertyType, depth + 1);

            // Check nullability on properties (mirrors IsNullableParameter logic)
            var isNullable = IsNullableProperty(prop);
            if (isNullable && !typeRef.IsNullable)
                typeRef = typeRef with { IsNullable = true };

            // Required = non-nullable reference type (no default on properties)
            var isRequired = !isNullable && !typeRef.IsNullable;

            props.Add(new Parameter(prop.Name, typeRef, isRequired));
        }
        return props;
    }

    private bool IsNullableProperty(PropertyInfo prop)
    {
        // Check for Nullable<T> (value types)
        if (prop.PropertyType.IsGenericType &&
            prop.PropertyType.GetGenericTypeDefinition().FullName == "System.Nullable`1")
            return true;

        // Check NullableAttribute on the property
        var nullableAttr = prop.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (nullableAttr != null)
        {
            var args = nullableAttr.ConstructorArguments;
            if (args.Count > 0)
            {
                if (args[0].Value is byte b && b == 2)
                    return true;
                if (args[0].Value is IReadOnlyCollection<CustomAttributeTypedArgument> arr)
                {
                    var first = arr.FirstOrDefault();
                    if (first.Value is byte fb && fb == 2)
                        return true;
                }
            }
        }

        // Check NullableContextAttribute on the declaring type for default context
        var contextAttr = prop.DeclaringType?.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute");
        if (contextAttr != null)
        {
            var args = contextAttr.ConstructorArguments;
            if (args.Count > 0 && args[0].Value is byte ctx && ctx == 2)
            {
                if (nullableAttr == null)
                    return true;
            }
        }

        return false;
    }

    private (TypeRef Type, bool IsStreaming) ExtractReturnType(MethodInfo method)
    {
        return UnwrapAndBuild(method.ReturnType);
    }

    private (TypeRef Type, bool IsStreaming) UnwrapAndBuild(Type type, bool isStreaming = false, int depth = 0)
    {
        if (depth > MaxTypeRecursionDepth)
            return (new TypeRef(TypeKind.Class, type.Name), isStreaming);

        // Unwrap Task<T>, ValueTask<T>, ClientResult<T>, IAsyncEnumerable<T>
        if (type.IsGenericType)
        {
            var genericName = StripArityFromName(type.Name);
            var args = type.GetGenericArguments();

            if (UnwrapTypes.Contains(genericName) && args.Length == 1)
                return UnwrapAndBuild(args[0], isStreaming, depth + 1);

            if (StreamingUnwrapTypes.Contains(genericName) && args.Length == 1)
                return UnwrapAndBuild(args[0], isStreaming: true, depth + 1);

            // Dictionary special case
            if (genericName == "Dictionary" && args.Length == 2)
                return (new TypeRef(TypeKind.Dictionary, "Dictionary"), isStreaming);

            // Nullable<T> (value type nullable)
            if (genericName == "Nullable" && args.Length == 1)
            {
                var inner = BuildTypeRef(args[0], depth + 1);
                return (inner with { IsNullable = true }, isStreaming);
            }

            // Generic type (List<T>, etc.)
            var genericArgs = args.Select(a => BuildTypeRef(a, depth + 1)).ToList();
            return (new TypeRef(TypeKind.Generic, genericName, GenericArguments: genericArgs), isStreaming);
        }

        return (BuildTypeRef(type, depth + 1), isStreaming);
    }

    private TypeRef BuildTypeRef(Type type, int depth = 0)
    {
        if (depth > MaxTypeRecursionDepth)
            return new TypeRef(TypeKind.Class, type.Name);

        // Handle Nullable<T> for value types
        if (type.IsGenericType)
        {
            var genericName = StripArityFromName(type.Name);

            if (genericName == "Nullable")
            {
                var inner = BuildTypeRef(type.GetGenericArguments()[0], depth + 1);
                return inner with { IsNullable = true };
            }

            // Dictionary
            var args = type.GetGenericArguments();
            if (genericName == "Dictionary" && args.Length == 2)
                return new TypeRef(TypeKind.Dictionary, "Dictionary");

            // Other generics
            var genericArgs = args.Select(a => BuildTypeRef(a, depth + 1)).ToList();
            return new TypeRef(TypeKind.Generic, genericName, GenericArguments: genericArgs);
        }

        // Enum
        if (type.IsEnum)
        {
            var values = type.GetEnumNames().ToList();
            return new TypeRef(TypeKind.Enum, type.Name, EnumValues: values);
        }

        // Array
        if (type.IsArray)
        {
            var elementType = BuildTypeRef(type.GetElementType()!, depth + 1);
            return new TypeRef(TypeKind.Array, type.Name, ElementType: elementType);
        }

        // Primitive
        if (IsPrimitiveType(type))
        {
            var name = PrimitiveAliases.TryGetValue(type.Name, out var alias) ? alias : type.Name;
            return new TypeRef(TypeKind.Primitive, name);
        }

        // Class
        return new TypeRef(TypeKind.Class, type.Name);
    }

    private static string StripArityFromName(string typeName)
    {
        var backtickIndex = typeName.IndexOf('`');
        return backtickIndex > 0 ? typeName[..backtickIndex] : typeName;
    }

    private bool IsPrimitiveType(Type type)
    {
        return type.IsPrimitive || PrimitiveTypes.Contains(type.Name);
    }

    private bool IsNullableParameter(ParameterInfo param)
    {
        // Check for Nullable<T> (value types)
        if (param.ParameterType.IsGenericType &&
            param.ParameterType.GetGenericTypeDefinition().FullName == "System.Nullable`1")
            return true;

        // Check for nullable reference types via custom attributes
        // NullableAttribute: byte constructor → single value; byte[] constructor → per-type-arg
        var nullableAttr = param.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (nullableAttr != null)
        {
            var args = nullableAttr.ConstructorArguments;
            if (args.Count > 0)
            {
                // Single byte form
                if (args[0].Value is byte b && b == 2)
                    return true;
                // Array form — first element is the top-level nullability
                if (args[0].Value is IReadOnlyCollection<CustomAttributeTypedArgument> arr)
                {
                    var first = arr.FirstOrDefault();
                    if (first.Value is byte fb && fb == 2)
                        return true;
                }
            }
        }

        // Check the method/type NullableContextAttribute for default context
        var contextAttr = param.Member.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute");
        if (contextAttr != null)
        {
            var args = contextAttr.ConstructorArguments;
            if (args.Count > 0 && args[0].Value is byte ctx && ctx == 2)
            {
                // Default context is nullable — if no per-parameter override, it's nullable
                if (nullableAttr == null)
                    return true;
            }
        }

        return false;
    }

    private IReadOnlyList<AuthPattern> DetectAuthPatterns(Assembly assembly, List<(string Noun, Type Type)> serviceClasses)
    {
        var patterns = new List<AuthPattern>();
        var seen = new HashSet<string>();

        foreach (var (_, type) in serviceClasses)
        {
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var param in ctor.GetParameters())
                {
                    var key = $"{param.ParameterType.Name}:{param.Name}";
                    if (seen.Contains(key)) continue;

                    // Check credential types first (more specific)
                    if (IsCredentialParameter(param))
                    {
                        seen.Add(key);
                        var envVar = GenerateEnvVarName(assembly, "token");
                        patterns.Add(new AuthPattern(AuthType.BearerToken, envVar, param.Name!));
                    }
                    else if (IsApiKeyParameter(param))
                    {
                        seen.Add(key);
                        var envVar = GenerateEnvVarName(assembly, param.Name!);
                        patterns.Add(new AuthPattern(AuthType.ApiKey, envVar, param.Name!));
                    }
                }
            }
        }

        return patterns;
    }

    private bool IsApiKeyParameter(ParameterInfo param)
    {
        if (param.ParameterType.FullName != "System.String") return false;
        var name = param.Name?.ToLowerInvariant() ?? "";
        return name.Contains("key") || name.Contains("secret");
    }

    private bool IsCredentialParameter(ParameterInfo param)
    {
        return param.ParameterType.Name.EndsWith("Credential", StringComparison.Ordinal);
    }

    private string GenerateEnvVarName(Assembly assembly, string suffix)
    {
        var name = assembly.GetName().Name ?? "SDK";
        // Strip common prefixes
        name = Regex.Replace(name, @"^CliBuilder\.", "");
        return $"{name.ToUpperInvariant().Replace(".", "_")}_{suffix.ToUpperInvariant()}";
    }

    /// <summary>
    /// Converts PascalCase to kebab-case, with acronym handling.
    /// Consecutive uppercase letters are treated as an acronym:
    /// "OpenAI" → "open-ai", "GetHTTPStatus" → "get-http-status"
    /// </summary>
    private static string PascalToKebabCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    var prevIsUpper = char.IsUpper(input[i - 1]);
                    var nextIsLower = i + 1 < input.Length && char.IsLower(input[i + 1]);

                    // Insert hyphen before:
                    // - a capital that follows a lowercase (standard boundary: "getStatus" → "get-Status")
                    // - a capital that is the start of a new word after an acronym ("HTTPStatus" → "HTTP-Status")
                    if (!prevIsUpper || nextIsLower)
                    {
                        sb.Append('-');
                    }
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
