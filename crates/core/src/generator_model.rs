//! Generator model types — language-neutral output of the model mapper.
//!
//! Field names are language-neutral: `cli_type` (not `CSharpType`),
//! `options_type_name` (not `OptionsClassName`), no `ConversionExpression`.
//! Generators add language-specific fields in their own model wrappers.

use crate::models::{TypeKind, TypeRef};
use serde::Serialize;

/// Language-specific behavior for model mapping.
/// Each generator implements this trait (Python keywords, C# keywords, etc.).
pub trait LanguageProfile {
    /// Map a TypeRef to a CLI type name.
    /// When `for_cli_param` is true, complex types should map to "string"
    /// (CLI options only accept primitive types; complex values come via --json-input).
    fn map_cli_type(&self, type_ref: &TypeRef, for_cli_param: bool) -> String;

    /// Map a primitive type name to the target language type.
    fn map_primitive_type(&self, name: &str) -> String;

    /// Build a type name for JSON deserialization targets.
    fn build_deserialization_type_name(&self, type_ref: &TypeRef) -> String;

    /// Check if a name is a language keyword.
    fn is_keyword(&self, name: &str) -> bool;

    /// Check if a name collides with generated boilerplate names.
    fn is_boilerplate_name(&self, name: &str) -> bool;

    /// Check if a type name represents a binary type that can't be deserialized.
    fn is_binary_type(&self, name: &str) -> bool;

    /// Check if a type name represents infrastructure/plumbing (not user-facing).
    fn is_infrastructure_type(&self, name: &str) -> bool;

    /// Check if a return type name represents a non-awaitable/unwirable type.
    fn is_unwirable_return_type(&self, name: &str) -> bool;
}

#[derive(Debug, Clone, Serialize)]
pub struct GeneratorModel {
    pub cli_name: String,
    pub sdk_name: String,
    pub sdk_version: String,
    pub sdk_package_name: String,
    pub root_namespace: String,
    pub cli_description: String,
    pub resources: Vec<ResourceModel>,
    pub auth: Option<AuthModel>,
    pub static_auth_setup: Option<String>,
    /// Which adapter discovery path produced the source metadata
    /// ("multi_service" or "single_client"). See ADR-023. Template-visible so
    /// generators can branch on it (e.g. emit a "sub-resources detected" note
    /// in the cli.py header for single_client mode).
    pub discovery_mode: String,
    /// True when discovery_mode == "single_client" AND at least one operation
    /// has a non-primitive return type (the "sub-resources detected but not
    /// expanded" condition — see ADR-023 consequences). Computed in
    /// ModelMapper::build. Templates use this to surface a documentation
    /// comment so end users know about the deferred capability.
    pub has_unexpanded_sub_resources: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct ResourceModel {
    pub name: String,
    pub class_name: String,
    pub description: Option<String>,
    pub operations: Vec<OperationModel>,
    pub source_class_name: Option<String>,
    pub source_module: Option<String>,
    pub can_construct: bool,
    pub constructor_auth: Option<ConstructorAuth>,
    pub constructor_config_params: Vec<ConstructorConfigParam>,
    pub required_modules: Vec<String>,
}

/// Auth parameter info for resource constructor.
/// Generators use this to build language-specific constructor expressions.
#[derive(Debug, Clone, Serialize)]
pub struct ConstructorAuth {
    pub param_name: String,
    pub type_name: String,
    pub type_module: Option<String>,
    /// True if the auth parameter is a plain string (no wrapping needed).
    pub is_plain_string: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct ConstructorConfigParam {
    pub cli_flag: String,
    pub var_name: String,
    pub cli_type: String,
    pub is_required: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct OperationModel {
    pub name: String,
    pub method_name: String,
    pub description: Option<String>,
    pub parameters: Vec<FlatParameter>,
    pub needs_json_input: bool,
    pub return_type_name: String,
    pub is_streaming: bool,
    pub source_method_name: Option<String>,
    pub options_type_name: Option<String>,
    pub method_params: Vec<MethodParamModel>,
    pub can_wire_sdk_call: bool,
    pub has_json_direct_params: bool,
    /// Flag for generators: when true, value-type options-class params need
    /// sentinel nullability so "user didn't provide" is distinguishable from
    /// "user set the default". Python ignores this; C# uses it for `?` suffixes.
    pub requires_sentinel_nullability: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct FlatParameter {
    pub cli_flag: String,
    pub property_name: String,
    /// Language-specific CLI type (set by LanguageProfile).
    pub cli_type: String,
    pub is_required: bool,
    /// Raw JSON default value. Generators convert to language-specific literals.
    pub default_value: Option<serde_json::Value>,
    pub description: Option<String>,
    pub enum_values: Option<Vec<String>>,
    pub sdk_type_name: Option<String>,
    pub sdk_type_kind: Option<TypeKind>,
    pub sdk_type_is_nullable: bool,
    pub sdk_type_is_extensible_enum: bool,
    pub source_options_class_name: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct MethodParamModel {
    /// Variable name for this parameter (not a full expression — generators add conversions).
    pub arg_name: String,
    pub type_name: Option<String>,
    /// SDK module path (e.g., "os.path", "Sdk.Models").
    pub module: Option<String>,
    pub is_options_class: bool,
    pub needs_json_deserialization: bool,
    pub deserialization_type_name: Option<String>,
    pub json_property_name: Option<String>,
    pub is_required: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct AuthModel {
    pub auth_type: String,
    pub env_var: String,
    pub parameter_name: String,
}
