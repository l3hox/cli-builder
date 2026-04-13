//! Model mapper — transforms SdkMetadata into a language-neutral GeneratorModel.
//! Takes a LanguageProfile trait object for language-specific type mapping and keywords.

use std::collections::{BTreeSet, HashSet};

use crate::generator_model::*;
use crate::identifier_validator::*;
use crate::models::*;
use crate::parameter_flattener;

/// Options for the model mapper.
pub struct MapperOptions {
    pub cli_name: Option<String>,
}

/// Build a GeneratorModel from SdkMetadata using the given language profile.
pub fn build(
    metadata: &SdkMetadata,
    options: &MapperOptions,
    profile: &dyn LanguageProfile,
) -> (GeneratorModel, Vec<Diagnostic>) {
    let mut diagnostics = Vec::new();
    let cli_name = options
        .cli_name
        .clone()
        .unwrap_or_else(|| derive_cli_name(&metadata.name));

    let static_auth_expr = metadata.static_auth.as_ref().and_then(|sa| {
        // Validate components before composing into expression (prevents code injection)
        if !is_valid_identifier(&sa.type_name) {
            diagnostics.push(Diagnostic {
                severity: DiagnosticSeverity::Warning,
                code: "CB205".to_string(),
                message: format!(
                    "Static auth type name '{}' is not a valid identifier — static auth disabled",
                    sa.type_name
                ),
            });
            return None;
        }
        if !is_valid_identifier(&sa.property_name) {
            diagnostics.push(Diagnostic {
                severity: DiagnosticSeverity::Warning,
                code: "CB205".to_string(),
                message: format!(
                    "Static auth property name '{}' is not a valid identifier — static auth disabled",
                    sa.property_name
                ),
            });
            return None;
        }
        if !sa.type_module.is_empty() && !is_valid_module_path(&sa.type_module) {
            diagnostics.push(Diagnostic {
                severity: DiagnosticSeverity::Warning,
                code: "CB205".to_string(),
                message: format!(
                    "Static auth module '{}' is not a valid module path — static auth disabled",
                    sa.type_module
                ),
            });
            return None;
        }
        if sa.type_module.is_empty() {
            Some(format!("{}.{}", sa.type_name, sa.property_name))
        } else {
            Some(format!("{}.{}.{}", sa.type_module, sa.type_name, sa.property_name))
        }
    });

    let resources: Vec<ResourceModel> = metadata
        .resources
        .iter()
        .map(|r| map_resource(r, profile, &mut diagnostics, static_auth_expr.as_deref()))
        .collect();

    let auth = metadata.auth_patterns.first().map(map_auth);

    let sdk_name = sanitize_string(Some(&metadata.name)).unwrap_or_default();
    let model = GeneratorModel {
        cli_description: sanitize_string(Some(&format!("{} — CLI for {}", &cli_name, &sdk_name)))
            .unwrap_or_default(),
        cli_name: cli_name.clone(),
        sdk_name: sdk_name.clone(),
        sdk_version: sanitize_string(Some(&metadata.version)).unwrap_or_default(),
        sdk_package_name: sdk_name,
        root_namespace: derive_namespace(&cli_name),
        resources,
        auth,
        static_auth_setup: static_auth_expr.and_then(|e| sanitize_string(Some(&e))),
    };

    (model, diagnostics)
}

/// Derive a CLI name from an SDK name.
/// "CliBuilder.TestSdk" -> "clibuilder-testsdk", "OpenAI" -> "openai"
pub fn derive_cli_name(sdk_name: &str) -> String {
    let name = sdk_name.replace('.', "-").replace(' ', "-").to_lowercase();
    let mut result = String::with_capacity(name.len());
    let mut prev_hyphen = false;
    for c in name.chars() {
        if c == '-' {
            if !prev_hyphen {
                result.push(c);
            }
            prev_hyphen = true;
        } else {
            result.push(c);
            prev_hyphen = false;
        }
    }
    result.trim_matches('-').to_string()
}

/// Derive a namespace/package name from CLI name.
pub fn derive_namespace(cli_name: &str) -> String {
    kebab_to_pascal(cli_name)
}

fn map_resource(
    resource: &Resource,
    profile: &dyn LanguageProfile,
    diagnostics: &mut Vec<Diagnostic>,
    static_auth_setup: Option<&str>,
) -> ResourceModel {
    let mut class_name = kebab_to_pascal(&resource.name);

    if !is_path_safe(&class_name) {
        let safe_name = sanitize_to_safe_name(&resource.name);
        diagnostics.push(Diagnostic {
            severity: DiagnosticSeverity::Warning,
            code: "CB204".to_string(),
            message: format!(
                "Resource name '{}' is not path-safe — sanitized to '{}'",
                resource.name, safe_name
            ),
        });
        class_name = safe_name;
    }

    if profile.is_keyword(&class_name.to_lowercase()) {
        diagnostics.push(Diagnostic {
            severity: DiagnosticSeverity::Info,
            code: "CB004".to_string(),
            message: format!(
                "Resource '{}' maps to language keyword '{}'",
                resource.name, class_name
            ),
        });
    }

    let operations: Vec<OperationModel> = resource
        .operations
        .iter()
        .map(|op| map_operation(op, profile, diagnostics))
        .collect();

    let (constructor_auth, constructor_config_params, can_construct) =
        build_constructor_info(resource, profile, diagnostics, static_auth_setup);

    // Collect required modules
    let mut modules = HashSet::new();
    if let Some(ref m) = resource.source_module {
        modules.insert(m.clone());
    }
    if let Some(ref params) = resource.constructor_params {
        for cp in params.iter().filter(|p| p.is_auth) {
            if let Some(ref m) = cp.type_module {
                modules.insert(m.clone());
            }
        }
    }
    for op in &operations {
        for mp in &op.method_params {
            if let Some(ref m) = mp.module {
                modules.insert(m.clone());
            }
        }
    }
    for src_op in &resource.operations {
        for p in &src_op.parameters {
            collect_generic_argument_modules(&p.type_ref, &mut modules);
        }
    }
    let required_modules: Vec<String> = modules
        .into_iter()
        .filter(|m| is_valid_module_path(m))
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();

    ResourceModel {
        name: resource.name.clone(),
        class_name,
        description: sanitize_string(resource.description.as_deref()),
        operations,
        source_class_name: sanitize_string(resource.source_class_name.as_deref()),
        source_module: sanitize_string(resource.source_module.as_deref()),
        can_construct,
        constructor_auth,
        constructor_config_params,
        required_modules,
    }
}

fn build_constructor_info(
    resource: &Resource,
    profile: &dyn LanguageProfile,
    diagnostics: &mut Vec<Diagnostic>,
    static_auth_setup: Option<&str>,
) -> (Option<ConstructorAuth>, Vec<ConstructorConfigParam>, bool) {
    let params = match &resource.constructor_params {
        Some(p) if !p.is_empty() => p,
        _ => {
            if static_auth_setup.is_some() && resource.has_parameterless_ctor {
                return (None, vec![], true);
            }
            return (None, vec![], false);
        }
    };

    let mut config_params = Vec::new();
    let mut auth = None;
    let mut has_auth = false;

    for p in params {
        if p.is_auth {
            has_auth = true;
            let is_string = p.type_name == "string";
            let is_valid = is_string || is_valid_identifier(&p.type_name);
            if !is_valid {
                diagnostics.push(Diagnostic {
                    severity: DiagnosticSeverity::Warning,
                    code: "CB205".to_string(),
                    message: format!(
                        "Constructor auth type '{}' is not a valid identifier — falling back to raw credential",
                        p.type_name
                    ),
                });
            }
            auth = Some(ConstructorAuth {
                param_name: p.name.clone(),
                type_name: if is_valid {
                    p.type_name.clone()
                } else {
                    "string".to_string()
                },
                type_module: p.type_module.clone(),
                is_plain_string: is_string || !is_valid,
            });
        } else if p.is_required {
            let (_, cli_flag, _) = sanitize_parameter(
                &p.name,
                |n| profile.is_keyword(n),
                |n| profile.is_boilerplate_name(n),
            );
            let var_name = format!("{}Value", kebab_to_camel_case(&cli_flag));
            let cli_type = profile.map_primitive_type(&p.type_name);
            config_params.push(ConstructorConfigParam {
                cli_flag,
                var_name,
                cli_type,
                is_required: true,
            });
        }
    }

    if !has_auth {
        return (None, vec![], false);
    }

    (auth, config_params, true)
}

fn map_operation(
    operation: &Operation,
    profile: &dyn LanguageProfile,
    diagnostics: &mut Vec<Diagnostic>,
) -> OperationModel {
    let method_name = kebab_to_pascal(&operation.name);
    let description = sanitize_string(operation.description.as_deref());
    let return_type_name = profile.map_cli_type(&operation.return_type, false);

    let flatten_result = parameter_flattener::flatten(&operation.parameters, profile, 10);
    diagnostics.extend(flatten_result.diagnostics);

    let method_params = build_method_params(&operation.parameters, profile);
    let can_wire = can_wire_operation(operation, profile, diagnostics);

    let has_json_direct_params = method_params.iter().any(|mp| mp.needs_json_deserialization);
    let needs_json_input = flatten_result.needs_json_input || has_json_direct_params;

    let options_type_name = operation
        .parameters
        .iter()
        .find(|p| p.type_ref.kind == TypeKind::Class && p.type_ref.properties.is_some())
        .and_then(|p| sanitize_string(Some(&p.type_ref.name)));

    OperationModel {
        name: operation.name.clone(),
        method_name,
        description,
        parameters: flatten_result.parameters,
        needs_json_input,
        return_type_name,
        is_streaming: operation.is_streaming,
        source_method_name: sanitize_string(operation.source_method_name.as_deref()),
        options_type_name,
        method_params,
        can_wire_sdk_call: can_wire,
        has_json_direct_params,
        requires_sentinel_nullability: needs_json_input
            && operation
                .parameters
                .iter()
                .any(|p| p.type_ref.kind == TypeKind::Class && p.type_ref.properties.is_some()),
    }
}

fn build_method_params(
    parameters: &[Parameter],
    profile: &dyn LanguageProfile,
) -> Vec<MethodParamModel> {
    let mut result = Vec::new();
    for p in parameters {
        if p.type_ref.kind == TypeKind::Class && p.type_ref.properties.is_some() {
            // Options class
            let type_name = sanitize_string(Some(&p.type_ref.name))
                .unwrap_or_else(|| p.type_ref.name.clone());
            result.push(MethodParamModel {
                arg_name: pascal_to_camel_case(&type_name),
                type_name: Some(type_name),
                module: sanitize_string(p.type_ref.module.as_deref()),
                is_options_class: true,
                needs_json_deserialization: false,
                deserialization_type_name: None,
                json_property_name: None,
                is_required: false,
            });
        } else if matches!(
            p.type_ref.kind,
            TypeKind::Generic | TypeKind::Array | TypeKind::Dictionary
        ) || (p.type_ref.kind == TypeKind::Class
            && p.type_ref.properties.is_none()
            && !profile.is_binary_type(&p.type_ref.name)
            && !profile.is_infrastructure_type(&p.type_ref.name))
        {
            // Complex direct param — JSON deserialization
            let (_, cli_flag, _) = sanitize_parameter(
                &p.name,
                |n| profile.is_keyword(n),
                |n| profile.is_boilerplate_name(n),
            );
            let deser_type = profile.build_deserialization_type_name(&p.type_ref);
            result.push(MethodParamModel {
                arg_name: format!("{}Value", kebab_to_camel_case(&cli_flag)),
                type_name: Some(deser_type.clone()),
                module: sanitize_string(p.type_ref.module.as_deref()),
                is_options_class: false,
                needs_json_deserialization: true,
                deserialization_type_name: Some(deser_type),
                json_property_name: Some(p.name.clone()),
                is_required: p.required,
            });
        } else if p.type_ref.kind == TypeKind::Enum && p.type_ref.is_extensible_enum {
            // Extensible enum — JSON deserialization
            let (_, cli_flag, _) = sanitize_parameter(
                &p.name,
                |n| profile.is_keyword(n),
                |n| profile.is_boilerplate_name(n),
            );
            result.push(MethodParamModel {
                arg_name: format!("{}Value", kebab_to_camel_case(&cli_flag)),
                type_name: Some(p.type_ref.name.clone()),
                module: sanitize_string(p.type_ref.module.as_deref()),
                is_options_class: false,
                needs_json_deserialization: true,
                deserialization_type_name: Some(p.type_ref.name.clone()),
                json_property_name: Some(p.name.clone()),
                is_required: false,
            });
        } else {
            // Primitive / real enum direct param
            let (_, cli_flag, _) = sanitize_parameter(
                &p.name,
                |n| profile.is_keyword(n),
                |n| profile.is_boilerplate_name(n),
            );
            result.push(MethodParamModel {
                arg_name: format!("{}Value", kebab_to_camel_case(&cli_flag)),
                type_name: None,
                module: if p.type_ref.kind == TypeKind::Enum {
                    sanitize_string(p.type_ref.module.as_deref())
                } else {
                    None
                },
                is_options_class: false,
                needs_json_deserialization: false,
                deserialization_type_name: None,
                json_property_name: None,
                is_required: false,
            });
        }
    }
    result
}

fn can_wire_operation(
    operation: &Operation,
    profile: &dyn LanguageProfile,
    diagnostics: &mut Vec<Diagnostic>,
) -> bool {
    for p in &operation.parameters {
        if p.type_ref.kind == TypeKind::Class && p.type_ref.properties.is_some() {
            continue; // Options class — handled by construction
        }

        let is_complex = matches!(
            p.type_ref.kind,
            TypeKind::Generic | TypeKind::Array | TypeKind::Dictionary
        ) || (p.type_ref.kind == TypeKind::Class && p.type_ref.properties.is_none());

        if is_complex {
            if profile.is_binary_type(&p.type_ref.name) {
                diagnostics.push(Diagnostic {
                    severity: DiagnosticSeverity::Info,
                    code: "CB306".to_string(),
                    message: format!(
                        "Operation '{}' has binary parameter '{}' ({}) — falling back to echo stub",
                        operation.name, p.name, p.type_ref.name
                    ),
                });
                return false;
            }

            if profile.is_infrastructure_type(&p.type_ref.name) {
                diagnostics.push(Diagnostic {
                    severity: DiagnosticSeverity::Info,
                    code: "CB306".to_string(),
                    message: format!(
                        "Operation '{}' has infrastructure parameter '{}' ({}) — falling back to echo stub",
                        operation.name, p.name, p.type_ref.name
                    ),
                });
                return false;
            }

            // Abstract type info diagnostic (not blocking)
            let is_abstract_container = p
                .type_ref
                .generic_arguments
                .as_ref()
                .map_or(false, |gas| gas.iter().any(|ga| ga.is_abstract));
            let is_abstract_direct =
                p.type_ref.kind == TypeKind::Class && p.type_ref.is_abstract;

            if is_abstract_container || is_abstract_direct {
                let inner_name = p
                    .type_ref
                    .generic_arguments
                    .as_ref()
                    .and_then(|gas| gas.iter().find(|ga| ga.is_abstract))
                    .map(|ga| ga.name.as_str())
                    .unwrap_or(&p.type_ref.name);
                diagnostics.push(Diagnostic {
                    severity: DiagnosticSeverity::Info,
                    code: "CB307".to_string(),
                    message: format!(
                        "Operation '{}' has abstract parameter '{}' ({}) — \
                         deserialization may require registered converters",
                        operation.name, p.name, inner_name
                    ),
                });
            }
        }
    }

    // Check return type
    if operation.return_type.kind == TypeKind::Class && !operation.is_streaming {
        if profile.is_unwirable_return_type(&operation.return_type.name) {
            diagnostics.push(Diagnostic {
                severity: DiagnosticSeverity::Warning,
                code: "CB306".to_string(),
                message: format!(
                    "Operation '{}' returns non-awaitable type '{}' — falling back to echo stub",
                    operation.name, operation.return_type.name
                ),
            });
            return false;
        }
    }

    true
}

fn collect_generic_argument_modules(type_ref: &TypeRef, modules: &mut HashSet<String>) {
    if let Some(ref gas) = type_ref.generic_arguments {
        for ga in gas {
            if let Some(ref m) = ga.module {
                modules.insert(m.clone());
            }
            collect_generic_argument_modules(ga, modules);
        }
    }
}

fn map_auth(pattern: &AuthPattern) -> AuthModel {
    AuthModel {
        auth_type: match pattern.auth_type {
            AuthType::ApiKey => "ApiKey",
            AuthType::BearerToken => "BearerToken",
            AuthType::OAuth => "OAuth",
            AuthType::Custom => "Custom",
        }
        .to_string(),
        env_var: sanitize_string(Some(&pattern.env_var)).unwrap_or_else(|| pattern.env_var.clone()),
        parameter_name: sanitize_string(Some(&pattern.parameter_name))
            .unwrap_or_else(|| pattern.parameter_name.clone()),
    }
}
