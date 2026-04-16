//! SdkMetadata model types — mirrors the C#/Python models for JSON compatibility.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SdkMetadata {
    pub name: String,
    pub version: String,
    pub resources: Vec<Resource>,
    pub auth_patterns: Vec<AuthPattern>,
    pub static_auth: Option<StaticAuthConfig>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Resource {
    pub name: String,
    pub description: Option<String>,
    pub operations: Vec<Operation>,
    pub source_class_name: Option<String>,
    pub source_module: Option<String>,
    pub constructor_params: Option<Vec<ConstructorParam>>,
    pub has_parameterless_ctor: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Operation {
    pub name: String,
    pub description: Option<String>,
    pub parameters: Vec<Parameter>,
    pub return_type: TypeRef,
    pub is_streaming: bool,
    pub source_method_name: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Parameter {
    pub name: String,
    #[serde(rename = "type")]
    pub type_ref: TypeRef,
    pub required: bool,
    pub default_value: Option<serde_json::Value>,
    pub description: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TypeRef {
    pub kind: TypeKind,
    pub name: String,
    pub is_nullable: bool,
    pub is_abstract: bool,
    pub is_extensible_enum: bool,
    pub generic_arguments: Option<Vec<TypeRef>>,
    pub enum_values: Option<Vec<String>>,
    pub properties: Option<Vec<Parameter>>,
    pub element_type: Option<Box<TypeRef>>,
    pub module: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum TypeKind {
    Primitive,
    Enum,
    Class,
    Generic,
    Array,
    Dictionary,
    Other,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ConstructorParam {
    pub name: String,
    pub type_name: String,
    pub type_module: Option<String>,
    pub is_auth: bool,
    pub is_required: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct StaticAuthConfig {
    pub type_name: String,
    pub type_module: String,
    pub property_name: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AuthPattern {
    #[serde(rename = "type")]
    pub auth_type: AuthType,
    pub env_var: String,
    pub parameter_name: String,
    pub header_name: Option<String>,
    pub description: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum AuthType {
    ApiKey,
    BearerToken,
    #[serde(rename = "oAuth")]
    OAuth,
    Custom,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum DiagnosticSeverity {
    Info,
    Warning,
    Error,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Diagnostic {
    pub severity: DiagnosticSeverity,
    pub code: String,
    pub message: String,
}

/// JSON envelope wrapping metadata + diagnostics + schema version.
/// `schema_version` is optional for backward compatibility with pre-Step-12 fixtures.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AdapterResultEnvelope {
    pub schema_version: Option<String>,
    pub metadata: SdkMetadata,
    pub diagnostics: Vec<Diagnostic>,
}
