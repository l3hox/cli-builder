using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CliBuilder.Core.Adapters;
using CliBuilder.Core.Models;

namespace CliBuilder.Adapter.DotNet;

public class DotNetAdapter : ISdkAdapter
{
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

        // Discover service classes
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
        var candidates = new Dictionary<string, List<Type>>();

        foreach (var type in assembly.GetExportedTypes())
        {
            var noun = TryExtractNoun(type.Name);
            if (noun == null) continue;

            if (!candidates.ContainsKey(noun))
                candidates[noun] = new List<Type>();
            candidates[noun].Add(type);
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
            if (!verbGroups.ContainsKey(verb))
                verbGroups[verb] = new List<MethodInfo>();
            verbGroups[verb].Add(method);
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
                var overloads = group.GroupBy(m => m.Name)
                    .SelectMany<IGrouping<string, MethodInfo>, MethodInfo>(g => g.Count() > 1
                        ? new[] { SelectRichestOverload(g.ToList(), verb, serviceType, diagnostics) }
                        : g.ToArray())
                    .ToList();

                // If we still have multiple (from different method names), pick the async one
                selected = overloads.FirstOrDefault(m => m.Name.EndsWith("Async")) ?? overloads[0];
            }
            else
            {
                selected = group[0];
            }

            var parameters = ExtractParameters(selected);
            var returnType = ExtractReturnType(selected);

            operations.Add(new Operation(verb, null, parameters, returnType));
        }

        return operations;
    }

    private MethodInfo SelectRichestOverload(List<MethodInfo> overloads, string verb, Type serviceType, List<Diagnostic> diagnostics)
    {
        var sorted = overloads.OrderByDescending(m =>
            m.GetParameters().Count(p => p.ParameterType.FullName != "System.Threading.CancellationToken")).ToList();

        if (sorted.Count > 1)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Info,
                "CB203",
                $"Overload disambiguated: '{sorted[0].Name}' on {serviceType.Name} has {sorted.Count} overloads for verb '{verb}'. Using the variant with {sorted[0].GetParameters().Length} parameters."));
        }

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
                var properties = ExtractClassProperties(param.ParameterType);
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

    private IReadOnlyList<Parameter> ExtractClassProperties(Type type)
    {
        var props = new List<Parameter>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var typeRef = BuildTypeRef(prop.PropertyType);
            props.Add(new Parameter(prop.Name, typeRef, false));
        }
        return props;
    }

    private TypeRef ExtractReturnType(MethodInfo method)
    {
        return UnwrapAndBuild(method.ReturnType);
    }

    private TypeRef UnwrapAndBuild(Type type)
    {
        // Unwrap Task<T>, ValueTask<T>, ClientResult<T>, IAsyncEnumerable<T>
        if (type.IsGenericType)
        {
            var genericName = type.Name;
            // Strip arity suffix (e.g., "Task`1" → "Task")
            var backtickIndex = genericName.IndexOf('`');
            if (backtickIndex > 0)
                genericName = genericName[..backtickIndex];

            var args = type.GetGenericArguments();

            if (UnwrapTypes.Contains(genericName) && args.Length == 1)
                return UnwrapAndBuild(args[0]);

            if (StreamingUnwrapTypes.Contains(genericName) && args.Length == 1)
                return UnwrapAndBuild(args[0]);

            // Dictionary special case
            if (genericName == "Dictionary" && args.Length == 2)
                return new TypeRef(TypeKind.Dictionary, "Dictionary");

            // Nullable<T> (value type nullable)
            if (genericName == "Nullable" && args.Length == 1)
            {
                var inner = BuildTypeRef(args[0]);
                return inner with { IsNullable = true };
            }

            // Generic type (List<T>, etc.)
            var genericArgs = args.Select(BuildTypeRef).ToList();
            return new TypeRef(TypeKind.Generic, genericName, GenericArguments: genericArgs);
        }

        return BuildTypeRef(type);
    }

    private TypeRef BuildTypeRef(Type type)
    {
        // Handle Nullable<T> for value types
        if (type.IsGenericType)
        {
            var genericName = type.Name;
            var backtickIndex = genericName.IndexOf('`');
            if (backtickIndex > 0)
                genericName = genericName[..backtickIndex];

            if (genericName == "Nullable")
            {
                var inner = BuildTypeRef(type.GetGenericArguments()[0]);
                return inner with { IsNullable = true };
            }

            // Dictionary
            var args = type.GetGenericArguments();
            if (genericName == "Dictionary" && args.Length == 2)
                return new TypeRef(TypeKind.Dictionary, "Dictionary");

            // Other generics
            var genericArgs = args.Select(BuildTypeRef).ToList();
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
            var elementType = BuildTypeRef(type.GetElementType()!);
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
        // NullableAttribute with byte value 2 means nullable
        var nullableAttr = param.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (nullableAttr != null)
        {
            var args = nullableAttr.ConstructorArguments;
            if (args.Count > 0)
            {
                if (args[0].Value is byte b && b == 2)
                    return true;
            }
        }

        // Check the parameter's NullableContextAttribute
        var contextAttr = param.Member.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute");
        if (contextAttr != null)
        {
            var args = contextAttr.ConstructorArguments;
            if (args.Count > 0 && args[0].Value is byte ctx && ctx == 2)
            {
                // Default context is nullable — check if parameter overrides
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

                    if (IsApiKeyParameter(param))
                    {
                        seen.Add(key);
                        var envVar = GenerateEnvVarName(assembly, param.Name!);
                        patterns.Add(new AuthPattern(AuthType.ApiKey, envVar, param.Name!));
                    }
                    else if (IsCredentialParameter(param))
                    {
                        seen.Add(key);
                        var envVar = GenerateEnvVarName(assembly, "token");
                        patterns.Add(new AuthPattern(AuthType.BearerToken, envVar, param.Name!));
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
        return name.Contains("key") || name.Contains("apikey") || name.Contains("token") || name.Contains("secret");
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

    private static string PascalToKebabCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('-');
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
