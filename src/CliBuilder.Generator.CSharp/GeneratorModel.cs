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
    string? SourceNamespace = null
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
    string? OptionsClassName = null
);

public record FlatParameter(
    string CliFlag,
    string PropertyName,
    string CSharpType,
    bool IsRequired,
    string? DefaultValueLiteral,
    string? Description,
    IReadOnlyList<string>? EnumValues
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
