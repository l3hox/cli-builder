//! Parameter flattening — converts SDK operation parameters into flat CLI flags.
//! Options classes are expanded into individual flags. Complex types trigger --json-input.

use crate::generator_model::*;
use crate::identifier_validator::{sanitize_parameter, sanitize_string};
use crate::models::*;

pub struct FlattenResult {
    pub parameters: Vec<FlatParameter>,
    pub needs_json_input: bool,
    pub diagnostics: Vec<Diagnostic>,
}

/// Flatten operation parameters into CLI flags.
pub fn flatten(
    parameters: &[Parameter],
    profile: &dyn LanguageProfile,
    threshold: usize,
) -> FlattenResult {
    let mut flat_params = Vec::new();
    let mut needs_json_input = false;
    let mut diagnostics = Vec::new();

    for param in parameters {
        if param.type_ref.kind == TypeKind::Class && param.type_ref.properties.is_some() {
            flatten_options_class(
                &param.type_ref,
                threshold,
                profile,
                &mut flat_params,
                &mut needs_json_input,
                &mut diagnostics,
            );
        } else if matches!(
            param.type_ref.kind,
            TypeKind::Generic | TypeKind::Array | TypeKind::Dictionary | TypeKind::Other
        ) || (param.type_ref.kind == TypeKind::Class && param.type_ref.properties.is_none())
            || (param.type_ref.kind == TypeKind::Enum && param.type_ref.is_extensible_enum)
        {
            needs_json_input = true;
        } else {
            flat_params.push(map_parameter(param, profile, &mut diagnostics, None));
        }
    }

    // Deduplicate by cli_flag — keep first occurrence
    let mut seen = std::collections::HashSet::new();
    let mut deduped = Vec::new();
    for fp in flat_params {
        if seen.insert(fp.cli_flag.clone()) {
            deduped.push(fp);
        } else if fp.is_required {
            diagnostics.push(Diagnostic {
                severity: DiagnosticSeverity::Warning,
                code: "CB303".to_string(),
                message: format!(
                    "Required parameter '--{}' duplicated across options classes — \
                     only the first occurrence is used.",
                    fp.cli_flag
                ),
            });
        }
    }

    FlattenResult {
        parameters: deduped,
        needs_json_input,
        diagnostics,
    }
}

fn is_scalar(type_ref: &TypeRef) -> bool {
    type_ref.kind == TypeKind::Primitive
        || (type_ref.kind == TypeKind::Enum && !type_ref.is_extensible_enum)
}

fn flatten_options_class(
    class_type: &TypeRef,
    threshold: usize,
    profile: &dyn LanguageProfile,
    flat_params: &mut Vec<FlatParameter>,
    needs_json_input: &mut bool,
    diagnostics: &mut Vec<Diagnostic>,
) {
    let properties = class_type.properties.as_ref().unwrap();
    let class_name = &class_type.name;

    let mut scalar_props: Vec<&Parameter> = properties.iter().filter(|p| is_scalar(&p.type_ref)).collect();
    // Sort: required first, then alphabetical
    scalar_props.sort_by(|a, b| b.required.cmp(&a.required).then_with(|| a.name.cmp(&b.name)));

    let has_nested = properties.iter().any(|p| !is_scalar(&p.type_ref));

    if has_nested {
        // Nested objects present -> always --json-input, flatten ALL scalar props
        *needs_json_input = true;
        for p in &scalar_props {
            flat_params.push(map_parameter(p, profile, diagnostics, Some(class_name)));
        }
    } else if scalar_props.len() > threshold {
        // Too many scalars -> flatten first {threshold}, add --json-input
        *needs_json_input = true;
        for p in scalar_props.iter().take(threshold) {
            flat_params.push(map_parameter(p, profile, diagnostics, Some(class_name)));
        }
        for p in scalar_props.iter().skip(threshold).filter(|p| p.required) {
            diagnostics.push(Diagnostic {
                severity: DiagnosticSeverity::Warning,
                code: "CB301".to_string(),
                message: format!(
                    "Required parameter '{}' is only accessible via --json-input due to flatten threshold.",
                    p.name
                ),
            });
        }
    } else {
        // All scalar, within threshold -> flatten all
        for p in &scalar_props {
            flat_params.push(map_parameter(p, profile, diagnostics, Some(class_name)));
        }
    }
}

fn map_parameter(
    param: &Parameter,
    profile: &dyn LanguageProfile,
    diagnostics: &mut Vec<Diagnostic>,
    source_options_class_name: Option<&str>,
) -> FlatParameter {
    let (property_name, cli_flag, diag) = sanitize_parameter(
        &param.name,
        |n| profile.is_keyword(n),
        |n| profile.is_boilerplate_name(n),
    );
    if let Some(d) = diag {
        diagnostics.push(d);
    }

    FlatParameter {
        cli_flag,
        property_name,
        cli_type: profile.map_cli_type(&param.type_ref, true),
        is_required: param.required,
        default_value: param.default_value.clone(),
        description: sanitize_string(param.description.as_deref()),
        enum_values: param.type_ref.enum_values.clone(),
        sdk_type_name: Some(param.type_ref.name.clone()),
        sdk_type_kind: Some(param.type_ref.kind.clone()),
        sdk_type_is_nullable: param.type_ref.is_nullable,
        sdk_type_is_extensible_enum: param.type_ref.is_extensible_enum,
        source_options_class_name: source_options_class_name.map(String::from),
    }
}
