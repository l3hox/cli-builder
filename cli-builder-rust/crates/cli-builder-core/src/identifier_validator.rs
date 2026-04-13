//! Identifier validation and case conversion utilities.
//! Provides language-neutral string manipulation with pluggable keyword checking
//! via closures (the LanguageProfile trait provides is_keyword/is_boilerplate_name).

use crate::models::{Diagnostic, DiagnosticSeverity};

/// Check if a string is a valid identifier (letters/digits/underscore, starts with letter or _).
pub fn is_valid_identifier(value: &str) -> bool {
    if value.is_empty() {
        return false;
    }
    let mut chars = value.chars();
    match chars.next() {
        Some(c) if c.is_alphabetic() || c == '_' => {}
        _ => return false,
    }
    chars.all(|c| c.is_alphanumeric() || c == '_')
}

/// Check if a string is a valid dotted module path (e.g., "os.path", "System.Collections").
pub fn is_valid_module_path(value: &str) -> bool {
    if value.is_empty() {
        return false;
    }
    value.split('.').all(is_valid_identifier)
}

/// Check if a name is safe for use in file paths.
pub fn is_path_safe(name: &str) -> bool {
    if name.is_empty() || name == "." || name.len() > 200 {
        return false;
    }
    if name.contains('/') || name.contains('\\') || name.contains("..") || name.contains('\0') {
        return false;
    }
    let base_name = name.split('.').next().unwrap_or(name);
    const RESERVED: &[&str] = &[
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];
    !RESERVED.iter().any(|r| r.eq_ignore_ascii_case(base_name))
}

/// PascalCase to kebab-case. "PaymentIntent" -> "payment-intent", "APIKey" -> "api-key"
pub fn pascal_to_kebab(pascal: &str) -> String {
    if pascal.is_empty() {
        return String::new();
    }
    let mut result = String::with_capacity(pascal.len() + 4);
    let chars: Vec<char> = pascal.chars().collect();
    for (i, &c) in chars.iter().enumerate() {
        if c.is_uppercase() && i > 0 {
            let prev_lower = chars[i - 1].is_lowercase();
            let prev_upper = chars[i - 1].is_uppercase();
            let next_lower = i + 1 < chars.len() && chars[i + 1].is_lowercase();
            if prev_lower || (prev_upper && next_lower) {
                result.push('-');
            }
        }
        for lc in c.to_lowercase() {
            result.push(lc);
        }
    }
    result
}

/// kebab-case to PascalCase. "payment-intent" -> "PaymentIntent"
pub fn kebab_to_pascal(kebab: &str) -> String {
    if kebab.is_empty() {
        return String::new();
    }
    kebab
        .split('-')
        .filter(|s| !s.is_empty())
        .map(|part| {
            let mut c = part.chars();
            match c.next() {
                Some(first) => {
                    let upper: String = first.to_uppercase().collect();
                    upper + c.as_str()
                }
                None => String::new(),
            }
        })
        .collect()
}

/// kebab-case to camelCase. "credit-limit" -> "creditLimit", "" -> "_param"
pub fn kebab_to_camel_case(value: &str) -> String {
    if value.is_empty() {
        return "_param".to_string();
    }
    let parts: Vec<&str> = value.split('-').filter(|s| !s.is_empty()).collect();
    if parts.is_empty() {
        return "_param".to_string();
    }
    let mut result = parts[0].to_string();
    for part in &parts[1..] {
        let mut c = part.chars();
        if let Some(first) = c.next() {
            let upper: String = first.to_uppercase().collect();
            result.push_str(&upper);
            result.push_str(c.as_str());
        }
    }
    result
}

/// PascalCase to camelCase. "CreateOptions" -> "createOptions"
pub fn pascal_to_camel_case(value: &str) -> String {
    if value.is_empty() {
        return value.to_string();
    }
    let mut chars = value.chars();
    match chars.next() {
        Some(first) => {
            let lower: String = first.to_lowercase().collect();
            lower + chars.as_str()
        }
        None => String::new(),
    }
}

/// Sanitize a parameter name for CLI use.
/// Returns (property_name, cli_flag, optional diagnostic).
/// The closures provide language-specific keyword and boilerplate checks.
pub fn sanitize_parameter(
    name: &str,
    is_keyword: impl Fn(&str) -> bool,
    is_boilerplate: impl Fn(&str) -> bool,
) -> (String, String, Option<Diagnostic>) {
    let kebab = pascal_to_kebab(name);
    let lower = name.to_lowercase();

    if is_keyword(&lower) {
        return (
            name.to_string(),
            format!("{}-value", kebab),
            Some(Diagnostic {
                severity: DiagnosticSeverity::Info,
                code: "CB004".to_string(),
                message: format!(
                    "Parameter '{}' is a language keyword — CLI flag '--{}-value'",
                    name, kebab
                ),
            }),
        );
    }

    if is_boilerplate(name) {
        return (
            name.to_string(),
            format!("{}-value", kebab),
            Some(Diagnostic {
                severity: DiagnosticSeverity::Info,
                code: "CB004".to_string(),
                message: format!(
                    "Parameter '{}' collides with generated name — CLI flag '--{}-value'",
                    name, kebab
                ),
            }),
        );
    }

    if !is_valid_identifier(name) {
        let safe = sanitize_to_identifier(name);
        let safe_kebab = pascal_to_kebab(&safe);
        return (
            safe.clone(),
            safe_kebab,
            Some(Diagnostic {
                severity: DiagnosticSeverity::Info,
                code: "CB204".to_string(),
                message: format!("Identifier '{}' sanitized to '{}'", name, safe),
            }),
        );
    }

    (name.to_string(), kebab, None)
}

/// Strip non-identifier characters from a string.
fn sanitize_to_identifier(name: &str) -> String {
    let mut result = String::new();
    for c in name.chars() {
        if result.is_empty() {
            if c.is_alphabetic() || c == '_' {
                result.push(c);
            } else {
                result.push('_');
            }
        } else if c.is_alphanumeric() || c == '_' {
            result.push(c);
        }
    }
    if result.is_empty() {
        "_invalid".to_string()
    } else {
        result
    }
}

/// Sanitize a resource name for path-safe usage.
pub fn sanitize_to_safe_name(name: &str) -> String {
    let safe: String = name
        .chars()
        .filter(|c| c.is_alphanumeric() || *c == '-' || *c == '_')
        .collect();
    let safe = if safe.is_empty() {
        "Unknown".to_string()
    } else {
        safe
    };
    kebab_to_pascal(&safe)
}

/// Core string sanitization — structural validation only.
/// Strips control characters (except newline, tab). Does NOT do template-engine
/// escaping (that belongs in generators per ADR-017 council decision).
pub fn sanitize_string(value: Option<&str>) -> Option<String> {
    value.map(|v| {
        v.chars()
            .filter(|c| !c.is_control() || *c == '\n' || *c == '\t')
            .collect()
    })
}
