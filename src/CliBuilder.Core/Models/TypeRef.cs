namespace CliBuilder.Core.Models;

public record TypeRef(
    TypeKind Kind,
    string Name,
    bool IsNullable = false,
    IReadOnlyList<TypeRef>? GenericArguments = null,
    IReadOnlyList<string>? EnumValues = null,
    IReadOnlyList<Parameter>? Properties = null,
    TypeRef? ElementType = null
);

public enum TypeKind
{
    Primitive,
    Enum,
    Class,
    Generic,
    Array,
    Dictionary
}
