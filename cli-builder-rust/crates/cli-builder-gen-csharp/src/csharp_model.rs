//! C#-specific model wrapper — post-processes the core GeneratorModel into
//! C#-ready types with ConversionExpression, DefaultValueLiteral, etc.

use cli_builder_core::generator_model::*;
use cli_builder_core::identifier_validator::{
    is_valid_identifier, pascal_to_camel_case, sanitize_string,
};
use cli_builder_core::models::*;
use regex::Regex;
use serde::Serialize;
use std::sync::LazyLock;

// ---- C#-specific model types ----

#[derive(Debug, Clone, Serialize)]
pub struct CSharpGeneratorModel {
    pub cli_name: String,
    pub sdk_name: String,
    pub sdk_version: String,
    pub sdk_package_name: String,
    pub root_namespace: String,
    pub cli_description: String,
    pub resources: Vec<CSharpResourceModel>,
    pub auth: Option<AuthModel>,
    pub static_auth_setup: Option<String>,
    pub sdk_project_path: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct CSharpResourceModel {
    pub name: String,
    pub class_name: String,
    pub description: Option<String>,
    pub operations: Vec<CSharpOperationModel>,
    pub source_class_name: Option<String>,
    pub source_module: Option<String>,
    pub can_construct: bool,
    pub constructor_expression: Option<String>,
    pub constructor_config_params: Vec<CSharpConstructorConfigParam>,
    pub required_namespaces: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct CSharpConstructorConfigParam {
    pub cli_flag: String,
    pub var_name: String,
    pub csharp_type: String,
    pub is_required: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct CSharpOperationModel {
    pub name: String,
    pub method_name: String,
    pub description: Option<String>,
    pub parameters: Vec<CSharpFlatParameter>,
    pub needs_json_input: bool,
    pub return_type_name: String,
    pub is_streaming: bool,
    pub source_method_name: Option<String>,
    pub options_type_name: Option<String>,
    pub method_params: Vec<CSharpMethodParam>,
    pub can_wire_sdk_call: bool,
    pub has_json_direct_params: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct CSharpFlatParameter {
    pub cli_flag: String,
    pub property_name: String,
    pub csharp_type: String,
    pub is_required: bool,
    pub default_value_literal: Option<String>,
    pub description: Option<String>,
    pub enum_values: Option<Vec<String>>,
    pub sdk_type_name: Option<String>,
    pub sdk_type_kind: Option<TypeKind>,
    pub sdk_type_is_nullable: bool,
    pub conversion_expression: Option<String>,
    pub source_options_class_name: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct CSharpMethodParam {
    pub arg_expression: String,
    pub type_name: Option<String>,
    pub namespace: Option<String>,
    pub is_options_class: bool,
    pub needs_json_deserialization: bool,
    pub deserialization_type_name: Option<String>,
    pub json_property_name: Option<String>,
    pub is_required: bool,
}

// ---- Post-processing pipeline ----

/// Convert a core GeneratorModel into a C#-specific model ready for Tera templates.
pub fn build_csharp_model(
    model: &GeneratorModel,
    diagnostics: &mut Vec<Diagnostic>,
) -> CSharpGeneratorModel {
    let resources = model
        .resources
        .iter()
        .map(|r| build_csharp_resource(r, diagnostics))
        .collect();

    CSharpGeneratorModel {
        cli_name: model.cli_name.clone(),
        sdk_name: sanitize_xml_value(&model.sdk_name),
        sdk_version: sanitize_xml_value(&model.sdk_version),
        sdk_package_name: sanitize_xml_value(&model.sdk_name),
        root_namespace: model.root_namespace.clone(),
        cli_description: sanitize_string(Some(&model.cli_description)).unwrap_or_default(),
        resources,
        auth: model.auth.clone(),
        static_auth_setup: sanitize_string(model.static_auth_setup.as_deref()),
        sdk_project_path: None,
    }
}

fn build_csharp_resource(
    resource: &ResourceModel,
    diagnostics: &mut Vec<Diagnostic>,
) -> CSharpResourceModel {
    let constructor_expression = build_constructor_expression(resource);

    let operations = resource
        .operations
        .iter()
        .map(|op| build_csharp_operation(op, diagnostics))
        .collect();

    let config_params = resource
        .constructor_config_params
        .iter()
        .map(|cp| CSharpConstructorConfigParam {
            cli_flag: cp.cli_flag.clone(),
            var_name: cp.var_name.clone(),
            csharp_type: cp.cli_type.clone(),
            is_required: cp.is_required,
        })
        .collect();

    CSharpResourceModel {
        name: resource.name.clone(),
        class_name: resource.class_name.clone(),
        description: resource.description.clone(),
        operations,
        source_class_name: resource.source_class_name.clone(),
        source_module: resource.source_module.clone(),
        can_construct: resource.can_construct,
        constructor_expression,
        constructor_config_params: config_params,
        required_namespaces: resource.required_modules.clone(),
    }
}

fn build_csharp_operation(
    op: &OperationModel,
    diagnostics: &mut Vec<Diagnostic>,
) -> CSharpOperationModel {
    let mut params: Vec<CSharpFlatParameter> = op
        .parameters
        .iter()
        .map(|p| build_csharp_flat_param(p, diagnostics))
        .collect();

    // MakeValueTypesNullable: when needs_json_input + has options class params
    if op.requires_sentinel_nullability {
        make_value_types_nullable(&mut params);
    }

    let method_params: Vec<CSharpMethodParam> = op
        .method_params
        .iter()
        .map(|mp| build_csharp_method_param(mp))
        .collect();

    CSharpOperationModel {
        name: op.name.clone(),
        method_name: op.method_name.clone(),
        description: op.description.clone(),
        parameters: params,
        needs_json_input: op.needs_json_input,
        return_type_name: op.return_type_name.clone(),
        is_streaming: op.is_streaming,
        source_method_name: op.source_method_name.clone(),
        options_type_name: op.options_type_name.clone(),
        method_params,
        can_wire_sdk_call: op.can_wire_sdk_call,
        has_json_direct_params: op.has_json_direct_params,
    }
}

pub(crate) fn build_csharp_flat_param(
    param: &FlatParameter,
    diagnostics: &mut Vec<Diagnostic>,
) -> CSharpFlatParameter {
    let conversion = compute_conversion(
        param.sdk_type_kind.as_ref(),
        param.sdk_type_name.as_deref(),
        param.sdk_type_is_nullable,
        param.cli_type == "string"
            && param.sdk_type_kind.as_ref() == Some(&TypeKind::Enum)
            && !param.sdk_type_is_extensible_enum,
    );

    let default_literal = param
        .default_value
        .as_ref()
        .and_then(|v| sanitize_default_value(v, param.sdk_type_name.as_deref(), diagnostics));

    // Prefix property_name with @ for C# keyword collision (e.g., "class" → "@class")
    let property_name = if crate::csharp_keywords::is_keyword(&param.property_name.to_lowercase()) {
        format!("@{}", param.property_name)
    } else {
        param.property_name.clone()
    };

    CSharpFlatParameter {
        cli_flag: param.cli_flag.clone(),
        property_name,
        csharp_type: param.cli_type.clone(),
        is_required: param.is_required,
        default_value_literal: default_literal,
        description: param.description.clone(),
        enum_values: param.enum_values.clone(),
        sdk_type_name: param.sdk_type_name.clone(),
        sdk_type_kind: param.sdk_type_kind.clone(),
        sdk_type_is_nullable: param.sdk_type_is_nullable,
        conversion_expression: conversion,
        source_options_class_name: param.source_options_class_name.clone(),
    }
}

fn build_csharp_method_param(mp: &MethodParamModel) -> CSharpMethodParam {
    let arg_expression = if mp.is_options_class {
        // Options class: PascalToCamelCase of type name
        mp.type_name
            .as_ref()
            .map(|tn| pascal_to_camel_case(tn))
            .unwrap_or_else(|| mp.arg_name.clone())
    } else if mp.needs_json_deserialization {
        // JSON deserialization: just the variable name
        mp.arg_name.clone()
    } else {
        // Direct param: may need enum conversion
        // The arg_name from core is already the variable name (e.g., "idValue")
        // Enum conversion is handled via the template's apply_conversion filter
        mp.arg_name.clone()
    };

    CSharpMethodParam {
        arg_expression,
        type_name: mp.type_name.clone(),
        namespace: mp.module.clone(),
        is_options_class: mp.is_options_class,
        needs_json_deserialization: mp.needs_json_deserialization,
        deserialization_type_name: mp.deserialization_type_name.clone(),
        json_property_name: mp.json_property_name.clone(),
        is_required: mp.is_required,
    }
}

// ---- Transform functions ----

static NUMERIC_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?$").unwrap()
});

const VALUE_TYPES: &[&str] = &[
    "bool", "int", "long", "short", "byte", "float", "double", "decimal",
];

/// Append `?` to value-type parameters from options classes when sentinel nullability is needed.
pub fn make_value_types_nullable(params: &mut [CSharpFlatParameter]) {
    for p in params.iter_mut() {
        if VALUE_TYPES.contains(&p.csharp_type.as_str()) && p.source_options_class_name.is_some() {
            let conversion = p.conversion_expression.clone().unwrap_or_else(|| "{0}.Value".to_string());
            p.csharp_type = format!("{}?", p.csharp_type);
            p.conversion_expression = Some(conversion);
        }
    }
}

/// Compute a C# conversion expression for assigning a CLI param to an SDK property.
/// Returns a format string with `{0}` as the value placeholder, or None (identity).
pub fn compute_conversion(
    sdk_type_kind: Option<&TypeKind>,
    sdk_type_name: Option<&str>,
    is_nullable: bool,
    is_enum_as_string: bool,
) -> Option<String> {
    if is_enum_as_string {
        let enum_name = sdk_type_name.unwrap_or("object");
        if !is_valid_identifier(enum_name) {
            return None;
        }
        return if is_nullable {
            Some(format!(
                "{{0}} is not null ? Enum.Parse<{0}>({{0}}) : ({0}?)null",
                enum_name
            ))
        } else {
            Some(format!("Enum.Parse<{}>({{0}})", enum_name))
        };
    }

    if sdk_type_kind == Some(&TypeKind::Primitive) {
        return match sdk_type_name.unwrap_or("") {
            "TimeSpan" => Some(if is_nullable {
                "{0} is not null ? TimeSpan.Parse({0}) : (TimeSpan?)null".to_string()
            } else {
                "TimeSpan.Parse({0})".to_string()
            }),
            "DateTime" => Some(if is_nullable {
                "{0} is not null ? DateTime.Parse({0}) : (DateTime?)null".to_string()
            } else {
                "DateTime.Parse({0})".to_string()
            }),
            "DateTimeOffset" => Some(if is_nullable {
                "{0} is not null ? DateTimeOffset.Parse({0}) : (DateTimeOffset?)null".to_string()
            } else {
                "DateTimeOffset.Parse({0})".to_string()
            }),
            "Guid" => Some(if is_nullable {
                "{0} is not null ? Guid.Parse({0}) : (Guid?)null".to_string()
            } else {
                "Guid.Parse({0})".to_string()
            }),
            _ => None,
        };
    }

    None
}

/// Convert a raw JSON default value to a C# literal string.
pub fn sanitize_default_value(
    value: &serde_json::Value,
    sdk_type_name: Option<&str>,
    diagnostics: &mut Vec<Diagnostic>,
) -> Option<String> {
    match value {
        serde_json::Value::Null => None,
        serde_json::Value::Bool(true) => Some("true".to_string()),
        serde_json::Value::Bool(false) => Some("false".to_string()),
        serde_json::Value::Number(n) => {
            let raw = n.to_string();
            if !NUMERIC_RE.is_match(&raw) {
                diagnostics.push(Diagnostic {
                    severity: DiagnosticSeverity::Warning,
                    code: "CB302".to_string(),
                    message: format!(
                        "Numeric default value '{}' failed format validation — ignored",
                        raw
                    ),
                });
                return None;
            }
            match sdk_type_name.unwrap_or("") {
                "decimal" | "Decimal" => Some(format!("{}m", raw)),
                "double" | "Double" => Some(format!("{}d", raw)),
                "float" | "Single" => Some(format!("{}f", raw)),
                _ => Some(raw),
            }
        }
        serde_json::Value::String(s) => Some(format!("@\"{}\"", escape_verbatim_string(s))),
        _ => {
            diagnostics.push(Diagnostic {
                severity: DiagnosticSeverity::Warning,
                code: "CB302".to_string(),
                message: format!(
                    "Default value of kind '{}' cannot be safely emitted — ignored",
                    value_kind_name(value)
                ),
            });
            None
        }
    }
}

/// Build a C# constructor expression from resource info.
pub fn build_constructor_expression(resource: &ResourceModel) -> Option<String> {
    let auth = match &resource.constructor_auth {
        Some(a) => a,
        None => {
            // No constructor auth — may have static auth with parameterless ctor
            return if resource.can_construct {
                Some(String::new()) // Empty expression = parameterless ctor
            } else {
                None
            };
        }
    };

    let mut arg_parts: Vec<String> = Vec::new();

    // Build config param args first
    for cp in &resource.constructor_config_params {
        arg_parts.push(cp.var_name.clone());
    }

    // Then auth arg
    if auth.is_plain_string {
        arg_parts.push("credential".to_string());
    } else if is_valid_identifier(&auth.type_name) {
        arg_parts.push(format!("new {}(credential)", auth.type_name));
    } else {
        arg_parts.push("credential".to_string());
    }

    Some(arg_parts.join(", "))
}

/// XML entity escaping for csproj values.
pub fn sanitize_xml_value(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

/// Escape a string for C# verbatim string literals: `"` → `""`.
pub fn escape_verbatim_string(value: &str) -> String {
    value.replace('"', "\"\"")
}

fn value_kind_name(value: &serde_json::Value) -> &'static str {
    match value {
        serde_json::Value::Array(_) => "Array",
        serde_json::Value::Object(_) => "Object",
        _ => "Unknown",
    }
}
