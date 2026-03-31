using CliBuilder.Core.Models;

namespace CliBuilder.Generator.CSharp;

public record GeneratorModel(
    string CliName,
    string SdkName,
    string SdkVersion,
    string SdkPackageName,
    string RootNamespace,
    string CliDescription,
    IReadOnlyList<ResourceModel> Resources,
    AuthModel? Auth,
    string? SdkProjectPath = null
);

public record ResourceModel(
    string Name,
    string ClassName,
    string? Description,
    IReadOnlyList<OperationModel> Operations,
    string? SourceClassName = null,
    string? SourceNamespace = null,
    string? ConstructorAuthExpression = null,
    IReadOnlyList<string>? RequiredNamespaces = null,
    bool CanConstruct = false
);

public record OperationModel(
    string Name,
    string MethodName,
    string? Description,
    IReadOnlyList<FlatParameter> Parameters,
    bool NeedsJsonInput,
    string ReturnTypeName,
    bool IsStreaming,
    string? SourceMethodName = null,
    string? OptionsClassName = null,
    IReadOnlyList<MethodParamModel>? MethodParams = null,
    bool CanWireSdkCall = true
);

public record FlatParameter(
    string CliFlag,
    string PropertyName,
    string CSharpType,
    bool IsRequired,
    string? DefaultValueLiteral,
    string? Description,
    IReadOnlyList<string>? EnumValues,
    string? SdkTypeName = null,
    TypeKind? SdkTypeKind = null,
    bool SdkTypeIsNullable = false,
    /// <summary>
    /// C# expression for converting a CLI param value to the SDK property type.
    /// Format string with {0} as the variable name placeholder.
    /// Examples: "Enum.Parse&lt;Status&gt;({0})", "TimeSpan.Parse({0})".
    /// Null means identity — no conversion needed (CLI type matches SDK type).
    /// The template's apply_conversion function substitutes {0} with "{varName}Value".
    /// </summary>
    string? ConversionExpression = null,
    string? SourceOptionsClassName = null
);

public record MethodParamModel(
    string ArgExpression,
    string? TypeName,
    string? Namespace,
    bool IsOptionsClass
);

public record AuthModel(
    string Type,
    string EnvVar,
    string ParameterName
);

public record CommandFileModel(
    string RootNamespace,
    ResourceModel Resource,
    bool HasAuth
);

public record AuthFileModel(
    string RootNamespace,
    string CliName,
    string EnvVar,
    string ParameterName
);

public record OutputFileModel(
    string RootNamespace
);
