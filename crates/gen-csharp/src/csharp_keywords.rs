//! C# keyword, contextual keyword, and boilerplate name lists.

use std::collections::HashSet;
use std::sync::LazyLock;

static CSHARP_KEYWORDS: LazyLock<HashSet<&'static str>> = LazyLock::new(|| {
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "continue", "decimal", "default",
        "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while",
    ]
    .into_iter()
    .collect()
});

static CONTEXTUAL_KEYWORDS: LazyLock<HashSet<&'static str>> = LazyLock::new(|| {
    [
        "var", "dynamic", "async", "await", "value", "get", "set",
        "add", "remove", "global", "partial", "where", "when", "yield",
        "nameof", "nint", "nuint", "record", "init", "required", "scoped", "file",
    ]
    .into_iter()
    .collect()
});

static BOILERPLATE_NAMES: LazyLock<HashSet<&'static str>> = LazyLock::new(|| {
    [
        "JsonFormatter", "TableFormatter", "AuthHandler", "Program",
        "apiKey", "json", "jsonInput", "credential", "ctx", "cmd", "command",
        "result", "useJson", "client", "items", "item",
    ]
    .into_iter()
    .collect()
});

pub fn is_keyword(name: &str) -> bool {
    let lower = name.to_lowercase();
    CSHARP_KEYWORDS.contains(lower.as_str()) || CONTEXTUAL_KEYWORDS.contains(lower.as_str())
}

pub fn is_boilerplate_name(name: &str) -> bool {
    BOILERPLATE_NAMES.contains(name)
}
