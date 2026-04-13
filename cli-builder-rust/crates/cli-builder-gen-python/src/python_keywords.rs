//! Python keyword and reserved name lists.

use std::collections::HashSet;
use std::sync::LazyLock;

static PYTHON_KEYWORDS: LazyLock<HashSet<&'static str>> = LazyLock::new(|| {
    [
        "false", "none", "true", "and", "as", "assert", "async", "await",
        "break", "class", "continue", "def", "del", "elif", "else",
        "except", "finally", "for", "from", "global", "if", "import",
        "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
        "return", "try", "while", "with", "yield",
    ]
    .into_iter()
    .collect()
});

static PYTHON_BUILTINS: LazyLock<HashSet<&'static str>> = LazyLock::new(|| {
    [
        "input", "list", "dict", "set", "type", "id", "str", "int",
        "float", "bool", "print", "open", "range", "len", "map",
        "filter", "zip", "enumerate", "sorted", "reversed", "sum",
        "min", "max", "abs", "round", "hash", "hex", "oct", "bin",
        "format", "object", "super", "property", "staticmethod",
        "classmethod", "isinstance", "issubclass", "callable", "iter",
        "next", "vars", "dir", "help", "exit", "quit",
    ]
    .into_iter()
    .collect()
});

static BOILERPLATE_NAMES: LazyLock<HashSet<&'static str>> = LazyLock::new(|| {
    [
        "json", "click", "ctx", "client", "result", "items", "item",
        "api_key", "credential", "use_json", "json_input", "cmd",
    ]
    .into_iter()
    .collect()
});

pub fn is_keyword(name: &str) -> bool {
    PYTHON_KEYWORDS.contains(name) || PYTHON_BUILTINS.contains(name)
}

pub fn is_boilerplate_name(name: &str) -> bool {
    BOILERPLATE_NAMES.contains(name)
}
