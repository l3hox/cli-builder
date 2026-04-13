//! Tests for SdkMetadata deserialization and model mapping.

use crate::generator_model::*;
use crate::identifier_validator::*;
use crate::model_mapper::{self, MapperOptions};
use crate::models::*;

/// Path to the .NET TestSdk fixture (relative to workspace root).
fn testsdk_fixture_path() -> std::path::PathBuf {
    let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    std::path::PathBuf::from(manifest_dir)
        .join("../../..")  // cli-builder-rust/crates/cli-builder-core → repo root
        .join("tests/fixtures/testsdk-metadata.json")
}

#[test]
fn deserialize_testsdk_fixture() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {}: {}", path.display(), e));
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json)
        .unwrap_or_else(|e| panic!("Failed to deserialize: {}", e));

    // schema_version may be absent in pre-Step-12 fixtures
    if let Some(ref v) = envelope.schema_version {
        assert_eq!(v, "1");
    }
    assert!(!envelope.metadata.resources.is_empty(), "Expected resources, got empty");
    assert!(envelope.metadata.resources.len() >= 7, "Expected >= 7 resources, got {}", envelope.metadata.resources.len());
}

#[test]
fn testsdk_has_expected_resources() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    let names: Vec<&str> = envelope.metadata.resources.iter().map(|r| r.name.as_str()).collect();
    assert!(names.contains(&"customer"), "Missing 'customer' resource");
    assert!(names.contains(&"order"), "Missing 'order' resource");
    assert!(names.contains(&"message"), "Missing 'message' resource");
}

#[test]
fn testsdk_customer_has_operations() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    let customer = envelope.metadata.resources.iter()
        .find(|r| r.name == "customer")
        .expect("customer resource not found");
    let op_names: Vec<&str> = customer.operations.iter().map(|o| o.name.as_str()).collect();
    assert!(op_names.contains(&"get"), "Missing 'get' operation");
    assert!(op_names.contains(&"create"), "Missing 'create' operation");
}

#[test]
fn testsdk_type_kinds_deserialize() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    // Find a primitive type (string parameter)
    let customer = envelope.metadata.resources.iter()
        .find(|r| r.name == "customer").unwrap();
    let get_op = customer.operations.iter()
        .find(|o| o.name == "get").unwrap();
    let id_param = &get_op.parameters[0];
    assert_eq!(id_param.type_ref.kind, TypeKind::Primitive);
}

#[test]
fn deserialize_python_adapter_output() {
    // Parse the Python adapter's JSON output if available
    let path = std::path::PathBuf::from("/tmp/python-adapter-output.json");
    if !path.exists() {
        eprintln!("Skipping: /tmp/python-adapter-output.json not found (run Python adapter first)");
        return;
    }
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json)
        .unwrap_or_else(|e| panic!("Failed to deserialize Python adapter output: {}", e));

    assert_eq!(envelope.schema_version.as_deref(), Some("1"));
    assert!(!envelope.metadata.resources.is_empty());
    assert!(envelope.metadata.resources.len() >= 3, "Expected >= 3 resources from Python TestSdk");
}

#[test]
fn testsdk_auth_patterns_deserialize() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    assert!(!envelope.metadata.auth_patterns.is_empty(), "Expected auth patterns");
    assert_eq!(envelope.metadata.auth_patterns[0].auth_type, AuthType::ApiKey);
}

// ================================================================
// Phase 2: Identifier Validator
// ================================================================

#[test]
fn pascal_to_kebab_conversions() {
    assert_eq!(pascal_to_kebab("PaymentIntent"), "payment-intent");
    assert_eq!(pascal_to_kebab("APIKey"), "api-key");
    assert_eq!(pascal_to_kebab("Customer"), "customer");
    assert_eq!(pascal_to_kebab(""), "");
}

#[test]
fn kebab_to_pascal_conversions() {
    assert_eq!(kebab_to_pascal("customer"), "Customer");
    assert_eq!(kebab_to_pascal("payment-intent"), "PaymentIntent");
    assert_eq!(kebab_to_pascal("order"), "Order");
    assert_eq!(kebab_to_pascal("get-metadata"), "GetMetadata");
    assert_eq!(kebab_to_pascal(""), "");
}

#[test]
fn kebab_to_camel_conversions() {
    assert_eq!(kebab_to_camel_case("id"), "id");
    assert_eq!(kebab_to_camel_case("credit-limit"), "creditLimit");
    assert_eq!(kebab_to_camel_case("api-key"), "apiKey");
    assert_eq!(kebab_to_camel_case(""), "_param");
}

#[test]
fn pascal_to_camel_conversions() {
    assert_eq!(pascal_to_camel_case("CreateCustomerOptions"), "createCustomerOptions");
    assert_eq!(pascal_to_camel_case("RequestOptions"), "requestOptions");
    assert_eq!(pascal_to_camel_case("A"), "a");
    assert_eq!(pascal_to_camel_case(""), "");
}

#[test]
fn path_safety_checks() {
    assert!(!is_path_safe("../etc"));
    assert!(!is_path_safe("foo/bar"));
    assert!(!is_path_safe("foo\\bar"));
    assert!(!is_path_safe(".."));
    assert!(!is_path_safe(""));
    assert!(!is_path_safe("CON"));
    assert!(is_path_safe("Customer"));
    assert!(is_path_safe("payment-intent"));
}

#[test]
fn identifier_validation() {
    assert!(is_valid_identifier("customer"));
    assert!(is_valid_identifier("_private"));
    assert!(is_valid_identifier("name123"));
    assert!(!is_valid_identifier(""));
    assert!(!is_valid_identifier("123abc"));
    assert!(!is_valid_identifier("foo-bar"));
}

#[test]
fn module_path_validation() {
    assert!(is_valid_module_path("Sdk.Models"));
    assert!(is_valid_module_path("os.path"));
    assert!(is_valid_module_path("Single"));
    assert!(!is_valid_module_path(""));
    assert!(!is_valid_module_path("foo.123"));
    assert!(!is_valid_module_path("foo..bar"));
}

// ================================================================
// Phase 2: Model Mapper — Test helpers and profile
// ================================================================

/// Test language profile that mimics C# behavior for testing with .NET fixtures.
struct TestProfile;

impl LanguageProfile for TestProfile {
    fn map_cli_type(&self, type_ref: &TypeRef, for_cli_param: bool) -> String {
        match type_ref.kind {
            TypeKind::Primitive => self.map_primitive_type(&type_ref.name),
            TypeKind::Enum => "string".to_string(),
            TypeKind::Class if for_cli_param => "string".to_string(),
            TypeKind::Class => type_ref.name.clone(),
            TypeKind::Array if for_cli_param => "string".to_string(),
            TypeKind::Array => format!(
                "{}[]",
                type_ref
                    .element_type
                    .as_ref()
                    .map(|et| self.map_cli_type(et, false))
                    .unwrap_or_else(|| "object".to_string())
            ),
            TypeKind::Dictionary => "string".to_string(),
            TypeKind::Generic if for_cli_param => "string".to_string(),
            TypeKind::Generic => type_ref.name.clone(),
            TypeKind::Other => "object".to_string(),
        }
    }

    fn map_primitive_type(&self, name: &str) -> String {
        match name {
            "string" | "String" => "string",
            "int" | "Int32" => "int",
            "long" | "Int64" => "long",
            "bool" | "Boolean" => "bool",
            "double" | "Double" => "double",
            "float" | "Single" => "float",
            "decimal" | "Decimal" => "decimal",
            "void" | "Void" => "void",
            _ => "string",
        }
        .to_string()
    }

    fn build_deserialization_type_name(&self, type_ref: &TypeRef) -> String {
        match type_ref.kind {
            TypeKind::Array => {
                let elem = type_ref
                    .element_type
                    .as_ref()
                    .map(|et| et.name.as_str())
                    .or_else(|| {
                        type_ref
                            .generic_arguments
                            .as_ref()
                            .and_then(|gas| gas.first())
                            .map(|ga| ga.name.as_str())
                    })
                    .unwrap_or("object");
                format!("{}[]", elem)
            }
            TypeKind::Dictionary => {
                if let Some(ref gas) = type_ref.generic_arguments {
                    if gas.len() == 2 {
                        return format!("Dictionary<{}, {}>", gas[0].name, gas[1].name);
                    }
                }
                "Dictionary<string, object>".to_string()
            }
            TypeKind::Generic => {
                if let Some(ref gas) = type_ref.generic_arguments {
                    if !gas.is_empty() {
                        if type_ref.name.contains("Dictionary") && gas.len() == 2 {
                            return format!("Dictionary<{}, {}>", gas[0].name, gas[1].name);
                        }
                        return format!("List<{}>", gas[0].name);
                    }
                }
                type_ref.name.clone()
            }
            _ => type_ref.name.clone(),
        }
    }

    fn is_keyword(&self, name: &str) -> bool {
        matches!(
            name,
            "class" | "int" | "string" | "bool" | "return" | "if" | "for" | "while"
        )
    }

    fn is_boilerplate_name(&self, _name: &str) -> bool {
        false
    }

    fn is_binary_type(&self, name: &str) -> bool {
        matches!(
            name,
            "BinaryContent" | "BinaryData" | "Stream" | "ReadOnlyMemory" | "ReadOnlySpan"
        )
    }

    fn is_infrastructure_type(&self, name: &str) -> bool {
        name == "RequestOptions"
            || name == "CancellationToken"
            || name.ends_with("ClientOptions")
            || name.ends_with("ClientSettings")
    }

    fn is_unwirable_return_type(&self, name: &str) -> bool {
        matches!(
            name,
            "AsyncCollectionResult" | "CollectionResult" | "Uri" | "Stream"
        ) || name.len() == 1
            || name.ends_with("Client")
            || name.ends_with("Service")
            || name.ends_with("Api")
            || name.ends_with("ClientSettings")
            || name.ends_with("Options")
            || name.ends_with("Response")
            || name.ends_with("Notification")
    }
}

fn opts(name: &str) -> MapperOptions {
    MapperOptions {
        cli_name: Some(name.to_string()),
    }
}

fn type_ref(kind: TypeKind, name: &str) -> TypeRef {
    TypeRef {
        kind,
        name: name.to_string(),
        is_nullable: false,
        is_abstract: false,
        is_extensible_enum: false,
        generic_arguments: None,
        enum_values: None,
        properties: None,
        element_type: None,
        module: None,
    }
}

fn make_op(name: &str, params: Vec<Parameter>, ret: TypeRef) -> Operation {
    Operation {
        name: name.to_string(),
        description: None,
        parameters: params,
        return_type: ret,
        is_streaming: false,
        source_method_name: None,
    }
}

fn make_param(name: &str, tr: TypeRef, required: bool) -> Parameter {
    Parameter {
        name: name.to_string(),
        type_ref: tr,
        required,
        default_value: None,
        description: None,
    }
}

fn make_resource(name: &str) -> Resource {
    Resource {
        name: name.to_string(),
        description: None,
        operations: vec![],
        source_class_name: None,
        source_module: None,
        constructor_params: None,
        has_parameterless_ctor: false,
    }
}

// ================================================================
// Phase 2: Model Mapper — Name conversion
// ================================================================

#[test]
fn build_converts_kebab_to_pascal_class_name() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![make_resource("customer"), make_resource("payment-intent")],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert_eq!(model.resources[0].class_name, "Customer");
    assert_eq!(model.resources[1].class_name, "PaymentIntent");
}

#[test]
fn build_null_description_remains_none() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![make_resource("test")],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.resources[0].description.is_none());
}

#[test]
fn build_path_unsafe_resource_emits_diagnostic() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![make_resource("../etc"), make_resource("foo/bar")],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, diags) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(diags.iter().any(|d| d.code == "CB204"));
    assert!(!model.resources[0].class_name.contains('/'));
    assert!(!model.resources[0].class_name.contains(".."));
}

#[test]
fn build_keyword_resource_emits_diagnostic() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![make_resource("class")],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (_, diags) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(diags.iter().any(|d| d.code == "CB004"));
}

// ================================================================
// Phase 2: Model Mapper — Auth mapping
// ================================================================

#[test]
fn build_maps_auth_patterns() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![AuthPattern {
            auth_type: AuthType::ApiKey,
            env_var: "MY_API_KEY".into(),
            parameter_name: "apiKey".into(),
            header_name: None,
            description: None,
        }],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    let auth = model.auth.unwrap();
    assert_eq!(auth.env_var, "MY_API_KEY");
}

#[test]
fn build_no_auth_is_none() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.auth.is_none());
}

// ================================================================
// Phase 2: Model Mapper — CLI name derivation
// ================================================================

#[test]
fn build_cli_name_from_options() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "OpenAI".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("openai-cli"), &profile);
    assert_eq!(model.cli_name, "openai-cli");
}

#[test]
fn build_derived_cli_name() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "CliBuilder.TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(
        &metadata,
        &MapperOptions { cli_name: None },
        &profile,
    );
    assert_eq!(model.cli_name, "clibuilder-testsdk");
}

// ================================================================
// Phase 2: Model Mapper — Operation mapping
// ================================================================

#[test]
fn build_maps_operations() {
    let profile = TestProfile;
    let op = make_op("create", vec![], type_ref(TypeKind::Class, "Customer"));
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "customer".into(),
            operations: vec![op],
            ..make_resource("customer")
        }],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert_eq!(model.resources[0].operations.len(), 1);
    assert_eq!(model.resources[0].operations[0].name, "create");
    assert_eq!(model.resources[0].operations[0].method_name, "Create");
}

// ================================================================
// Phase 2: Model Mapper — Constructor info
// ================================================================

#[test]
fn constructor_string_auth() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "customer".into(),
            source_class_name: Some("CustomerService".into()),
            source_module: Some("Test.Services".into()),
            constructor_params: Some(vec![ConstructorParam {
                name: "apiKey".into(),
                type_name: "string".into(),
                type_module: None,
                is_auth: true,
                is_required: true,
            }]),
            ..make_resource("customer")
        }],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.resources[0].can_construct);
    let auth = model.resources[0].constructor_auth.as_ref().unwrap();
    assert!(auth.is_plain_string);
    assert_eq!(auth.type_name, "string");
}

#[test]
fn constructor_typed_auth() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "product".into(),
            source_class_name: Some("ProductApi".into()),
            source_module: Some("Test.Services".into()),
            constructor_params: Some(vec![ConstructorParam {
                name: "credential".into(),
                type_name: "TokenCredential".into(),
                type_module: Some("Test.Models".into()),
                is_auth: true,
                is_required: true,
            }]),
            ..make_resource("product")
        }],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.resources[0].can_construct);
    let auth = model.resources[0].constructor_auth.as_ref().unwrap();
    assert!(!auth.is_plain_string);
    assert_eq!(auth.type_name, "TokenCredential");
}

#[test]
fn constructor_no_auth_cannot_construct() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![make_resource("thing")],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(!model.resources[0].can_construct);
}

#[test]
fn constructor_multi_arg_with_config_params() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "search".into(),
            source_class_name: Some("SearchClient".into()),
            source_module: Some("Sdk".into()),
            constructor_params: Some(vec![
                ConstructorParam {
                    name: "index".into(),
                    type_name: "String".into(),
                    type_module: None,
                    is_auth: false,
                    is_required: true,
                },
                ConstructorParam {
                    name: "credential".into(),
                    type_name: "ApiKeyCredential".into(),
                    type_module: Some("Sdk.Auth".into()),
                    is_auth: true,
                    is_required: true,
                },
            ]),
            ..make_resource("search")
        }],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.resources[0].can_construct);
    assert_eq!(model.resources[0].constructor_config_params.len(), 1);
    let cp = &model.resources[0].constructor_config_params[0];
    assert_eq!(cp.cli_flag, "index");
    assert_eq!(cp.var_name, "indexValue");
    assert!(cp.is_required);
}

#[test]
fn constructor_invalid_auth_type_emits_diagnostic() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "thing".into(),
            source_class_name: Some("ThingService".into()),
            source_module: Some("Sdk".into()),
            constructor_params: Some(vec![ConstructorParam {
                name: "cred".into(),
                type_name: "123BadType".into(),
                type_module: None,
                is_auth: true,
                is_required: true,
            }]),
            ..make_resource("thing")
        }],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, diags) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    let auth = model.resources[0].constructor_auth.as_ref().unwrap();
    assert!(auth.is_plain_string); // Falls back to string
    assert!(diags.iter().any(|d| d.code == "CB205"));
}

#[test]
fn static_auth_parameterless_ctor_can_construct() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "charge".into(),
            source_class_name: Some("ChargeService".into()),
            source_module: Some("Stripe".into()),
            has_parameterless_ctor: true,
            ..make_resource("charge")
        }],
        auth_patterns: vec![],
        static_auth: Some(StaticAuthConfig {
            type_name: "StripeConfiguration".into(),
            type_module: "Stripe".into(),
            property_name: "ApiKey".into(),
        }),
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.resources[0].can_construct);
    assert!(model.resources[0].constructor_auth.is_none());
}

#[test]
fn static_auth_setup_expression() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![],
        static_auth: Some(StaticAuthConfig {
            type_name: "Config".into(),
            type_module: "Sdk".into(),
            property_name: "ApiKey".into(),
        }),
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert_eq!(model.static_auth_setup.as_deref(), Some("Sdk.Config.ApiKey"));
}

#[test]
fn static_auth_empty_module_no_leading_dot() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![],
        static_auth: Some(StaticAuthConfig {
            type_name: "GlobalConfig".into(),
            type_module: "".into(),
            property_name: "ApiKey".into(),
        }),
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert_eq!(model.static_auth_setup.as_deref(), Some("GlobalConfig.ApiKey"));
}

// ================================================================
// Phase 2: Model Mapper — CanWireSdkCall
// ================================================================

fn build_operation_model(params: Vec<Parameter>, ret: TypeRef) -> OperationModel {
    build_operation_model_streaming(params, ret, false)
}

fn build_operation_model_streaming(
    params: Vec<Parameter>,
    ret: TypeRef,
    is_streaming: bool,
) -> OperationModel {
    let profile = TestProfile;
    let op = Operation {
        name: "test-op".into(),
        description: None,
        parameters: params,
        return_type: ret,
        is_streaming,
        source_method_name: Some("TestAsync".into()),
    };
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "thing".into(),
            source_class_name: Some("ThingService".into()),
            source_module: Some("Sdk".into()),
            operations: vec![op],
            constructor_params: Some(vec![ConstructorParam {
                name: "apiKey".into(),
                type_name: "string".into(),
                type_module: None,
                is_auth: true,
                is_required: true,
            }]),
            ..make_resource("thing")
        }],
        auth_patterns: vec![AuthPattern {
            auth_type: AuthType::ApiKey,
            env_var: "TEST_KEY".into(),
            parameter_name: "apiKey".into(),
            header_name: None,
            description: None,
        }],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    model.resources[0].operations[0].clone()
}

#[test]
fn can_wire_primitive_params() {
    let op = build_operation_model(
        vec![make_param("id", type_ref(TypeKind::Primitive, "string"), true)],
        type_ref(TypeKind::Class, "Customer"),
    );
    assert!(op.can_wire_sdk_call);
}

#[test]
fn can_wire_enum_param() {
    let mut tr = type_ref(TypeKind::Enum, "Status");
    tr.enum_values = Some(vec!["Active".into()]);
    let op = build_operation_model(
        vec![make_param("status", tr, true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert!(op.can_wire_sdk_call);
}

#[test]
fn can_wire_options_class() {
    let mut opts_type = type_ref(TypeKind::Class, "Opts");
    opts_type.properties = Some(vec![make_param(
        "X",
        type_ref(TypeKind::Primitive, "string"),
        true,
    )]);
    let op = build_operation_model(
        vec![make_param("opts", opts_type, true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert!(op.can_wire_sdk_call);
}

#[test]
fn cannot_wire_binary_content_param() {
    let op = build_operation_model(
        vec![make_param("content", type_ref(TypeKind::Class, "BinaryContent"), true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert!(!op.can_wire_sdk_call);
}

#[test]
fn cannot_wire_other_binary_types() {
    for name in &["BinaryData", "Stream", "ReadOnlyMemory", "ReadOnlySpan"] {
        let op = build_operation_model(
            vec![make_param("data", type_ref(TypeKind::Class, name), true)],
            type_ref(TypeKind::Primitive, "void"),
        );
        assert!(!op.can_wire_sdk_call, "Expected false for {}", name);
    }
}

#[test]
fn cannot_wire_async_collection_result_return() {
    let op = build_operation_model(
        vec![],
        type_ref(TypeKind::Class, "AsyncCollectionResult"),
    );
    assert!(!op.can_wire_sdk_call);
}

#[test]
fn cannot_wire_client_return() {
    let op = build_operation_model(vec![], type_ref(TypeKind::Class, "ChatClient"));
    assert!(!op.can_wire_sdk_call);
}

#[test]
fn can_wire_normal_class_return() {
    let op = build_operation_model(vec![], type_ref(TypeKind::Class, "Customer"));
    assert!(op.can_wire_sdk_call);
}

#[test]
fn can_wire_streaming_return() {
    let op = build_operation_model_streaming(
        vec![],
        type_ref(TypeKind::Class, "Customer"),
        true,
    );
    assert!(op.can_wire_sdk_call);
}

#[test]
fn abstract_generic_arg_emits_cb307() {
    let profile = TestProfile;
    let mut inner = type_ref(TypeKind::Class, "ChatMessage");
    inner.is_abstract = true;
    inner.module = Some("OpenAI.Chat".into());
    let mut generic = type_ref(TypeKind::Generic, "IEnumerable");
    generic.generic_arguments = Some(vec![inner]);
    let op = Operation {
        name: "test-op".into(),
        description: None,
        parameters: vec![make_param("messages", generic, true)],
        return_type: type_ref(TypeKind::Primitive, "void"),
        is_streaming: false,
        source_method_name: Some("TestAsync".into()),
    };
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "thing".into(),
            operations: vec![op],
            ..make_resource("thing")
        }],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (_, diags) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(diags.iter().any(|d| d.code == "CB307"));
}

// ================================================================
// Phase 2: Model Mapper — MethodParams
// ================================================================

#[test]
fn method_params_options_class() {
    let mut opts_type = type_ref(TypeKind::Class, "CreateOptions");
    opts_type.properties = Some(vec![make_param(
        "Name",
        type_ref(TypeKind::Primitive, "string"),
        true,
    )]);
    opts_type.module = Some("Sdk.Models".into());
    let op = build_operation_model(
        vec![make_param("options", opts_type, true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    let mp = &op.method_params;
    assert_eq!(mp.len(), 1);
    assert!(mp[0].is_options_class);
    assert_eq!(mp[0].type_name.as_deref(), Some("CreateOptions"));
    assert_eq!(mp[0].arg_name, "createOptions");
    assert_eq!(mp[0].module.as_deref(), Some("Sdk.Models"));
}

#[test]
fn method_params_direct_param() {
    let op = build_operation_model(
        vec![make_param("id", type_ref(TypeKind::Primitive, "string"), true)],
        type_ref(TypeKind::Class, "Customer"),
    );
    let mp = &op.method_params;
    assert_eq!(mp.len(), 1);
    assert!(!mp[0].is_options_class);
    assert_eq!(mp[0].arg_name, "idValue");
}

#[test]
fn method_params_generic_needs_json_deserialization() {
    let mut generic = type_ref(TypeKind::Generic, "IEnumerable");
    generic.generic_arguments = Some(vec![type_ref(TypeKind::Primitive, "string")]);
    let op = build_operation_model(
        vec![make_param("ids", generic, true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    let mp = &op.method_params[0];
    assert!(mp.needs_json_deserialization);
    assert_eq!(mp.deserialization_type_name.as_deref(), Some("List<string>"));
    assert_eq!(mp.json_property_name.as_deref(), Some("ids"));
}

// ================================================================
// Phase 2: Model Mapper — Parameter flattening
// ================================================================

#[test]
fn flatten_primitive_params() {
    let op = build_operation_model(
        vec![
            make_param("id", type_ref(TypeKind::Primitive, "string"), true),
            make_param("name", type_ref(TypeKind::Primitive, "string"), false),
        ],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert_eq!(op.parameters.len(), 2);
    assert_eq!(op.parameters[0].cli_flag, "id");
    assert_eq!(op.parameters[1].cli_flag, "name");
    assert!(!op.needs_json_input);
}

#[test]
fn flatten_options_class_within_threshold() {
    let mut opts_type = type_ref(TypeKind::Class, "Opts");
    opts_type.properties = Some(vec![
        make_param("Name", type_ref(TypeKind::Primitive, "string"), true),
        make_param("Age", type_ref(TypeKind::Primitive, "int"), false),
    ]);
    let op = build_operation_model(
        vec![make_param("opts", opts_type, true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert_eq!(op.parameters.len(), 2);
    assert!(!op.needs_json_input);
}

#[test]
fn flatten_options_class_with_nested_triggers_json_input() {
    let nested = type_ref(TypeKind::Class, "Address");
    let mut opts_type = type_ref(TypeKind::Class, "Opts");
    opts_type.properties = Some(vec![
        make_param("Name", type_ref(TypeKind::Primitive, "string"), true),
        make_param("Address", nested, false),
    ]);
    let op = build_operation_model(
        vec![make_param("opts", opts_type, true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    // Scalar props flattened, but needs_json_input because of nested
    assert_eq!(op.parameters.len(), 1); // Only "Name" (scalar)
    assert!(op.needs_json_input);
}

// ================================================================
// Council fixes: sanitize_string contract
// ================================================================

#[test]
fn sanitize_string_strips_control_chars() {
    let result = sanitize_string(Some("hello\x00world\x07!"));
    assert_eq!(result.as_deref(), Some("helloworld!"));
}

#[test]
fn sanitize_string_preserves_newline_and_tab() {
    let result = sanitize_string(Some("line1\nline2\ttab"));
    assert_eq!(result.as_deref(), Some("line1\nline2\ttab"));
}

#[test]
fn sanitize_string_passes_through_template_syntax() {
    // Core does NOT escape template syntax — that's the generators' responsibility (ADR-017)
    let result = sanitize_string(Some("Use {{ env 'SECRET' }} here"));
    assert_eq!(result.as_deref(), Some("Use {{ env 'SECRET' }} here"));
    let result2 = sanitize_string(Some("{%if x%}{{y}}{%endif%}"));
    assert_eq!(result2.as_deref(), Some("{%if x%}{{y}}{%endif%}"));
}

#[test]
fn sanitize_string_passes_through_shell_and_quote_chars() {
    let result = sanitize_string(Some("$HOME; rm -rf / `cmd` \"quoted\""));
    assert_eq!(result.as_deref(), Some("$HOME; rm -rf / `cmd` \"quoted\""));
}

#[test]
fn sanitize_string_none_returns_none() {
    assert!(sanitize_string(None).is_none());
}

// ================================================================
// Council fixes: auth_type string stability
// ================================================================

#[test]
fn auth_type_produces_stable_strings() {
    let profile = TestProfile;
    for (auth_type, expected) in [
        (AuthType::ApiKey, "ApiKey"),
        (AuthType::BearerToken, "BearerToken"),
        (AuthType::OAuth, "OAuth"),
        (AuthType::Custom, "Custom"),
    ] {
        let metadata = SdkMetadata {
            name: "TestSdk".into(),
            version: "1.0.0".into(),
            resources: vec![],
            auth_patterns: vec![AuthPattern {
                auth_type,
                env_var: "KEY".into(),
                parameter_name: "key".into(),
                header_name: None,
                description: None,
            }],
            static_auth: None,
        };
        let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
        assert_eq!(model.auth.as_ref().unwrap().auth_type, expected);
    }
}

// ================================================================
// Council fixes: static_auth identifier validation
// ================================================================

#[test]
fn static_auth_invalid_type_name_emits_diagnostic() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![],
        static_auth: Some(StaticAuthConfig {
            type_name: "Foo(); import os".into(),
            type_module: "Sdk".into(),
            property_name: "ApiKey".into(),
        }),
    };
    let (model, diags) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.static_auth_setup.is_none());
    assert!(diags.iter().any(|d| d.code == "CB205"));
}

#[test]
fn static_auth_invalid_property_name_emits_diagnostic() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![],
        static_auth: Some(StaticAuthConfig {
            type_name: "Config".into(),
            type_module: "".into(),
            property_name: "key; drop()".into(),
        }),
    };
    let (model, diags) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.static_auth_setup.is_none());
    assert!(diags.iter().any(|d| d.code == "CB205"));
}

#[test]
fn static_auth_invalid_module_emits_diagnostic() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![],
        auth_patterns: vec![],
        static_auth: Some(StaticAuthConfig {
            type_name: "Config".into(),
            type_module: "Sdk..Bad".into(),
            property_name: "ApiKey".into(),
        }),
    };
    let (model, diags) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.static_auth_setup.is_none());
    assert!(diags.iter().any(|d| d.code == "CB205"));
}

// ================================================================
// Council fixes: requires_sentinel_nullability
// ================================================================

#[test]
fn sentinel_nullability_false_for_pure_primitives() {
    let op = build_operation_model(
        vec![make_param("id", type_ref(TypeKind::Primitive, "string"), true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert!(!op.requires_sentinel_nullability);
}

#[test]
fn sentinel_nullability_false_for_json_direct_without_options_class() {
    // A direct generic param triggers needs_json_input, but without an options class
    // requires_sentinel_nullability should be false
    let mut generic = type_ref(TypeKind::Generic, "IEnumerable");
    generic.generic_arguments = Some(vec![type_ref(TypeKind::Primitive, "string")]);
    let op = build_operation_model(
        vec![make_param("ids", generic, true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert!(op.needs_json_input);
    assert!(!op.requires_sentinel_nullability);
}

#[test]
fn sentinel_nullability_true_with_nested_options_class() {
    let nested = type_ref(TypeKind::Class, "Address");
    let mut opts_type = type_ref(TypeKind::Class, "Opts");
    opts_type.properties = Some(vec![
        make_param("Name", type_ref(TypeKind::Primitive, "string"), true),
        make_param("Addr", nested, false),
    ]);
    let op = build_operation_model(
        vec![make_param("opts", opts_type, true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert!(op.needs_json_input);
    assert!(op.requires_sentinel_nullability);
}

// ================================================================
// Council fixes: negative static-auth tests
// ================================================================

#[test]
fn static_auth_no_parameterless_ctor_cannot_construct() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "nested".into(),
            source_class_name: Some("NestedService".into()),
            source_module: Some("Stripe".into()),
            has_parameterless_ctor: false,
            ..make_resource("nested")
        }],
        auth_patterns: vec![],
        static_auth: Some(StaticAuthConfig {
            type_name: "StripeConfiguration".into(),
            type_module: "Stripe".into(),
            property_name: "ApiKey".into(),
        }),
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(!model.resources[0].can_construct);
}

#[test]
fn no_static_auth_parameterless_ctor_still_cannot_construct() {
    let profile = TestProfile;
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "thing".into(),
            has_parameterless_ctor: true,
            ..make_resource("thing")
        }],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, _) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(!model.resources[0].can_construct);
}

// ================================================================
// Council fixes: additional CanWireSdkCall coverage
// ================================================================

#[test]
fn can_wire_non_binary_bare_class_param() {
    let op = build_operation_model(
        vec![make_param("item", type_ref(TypeKind::Class, "RealtimeItem"), true)],
        type_ref(TypeKind::Primitive, "void"),
    );
    assert!(op.can_wire_sdk_call);
}

#[test]
fn cannot_wire_collection_result_return() {
    let op = build_operation_model(vec![], type_ref(TypeKind::Class, "CollectionResult"));
    assert!(!op.can_wire_sdk_call);
}

#[test]
fn can_wire_generic_with_concrete_arg_no_cb307() {
    let profile = TestProfile;
    let mut inner = type_ref(TypeKind::Class, "UserMessage");
    inner.is_abstract = false;
    let mut generic = type_ref(TypeKind::Generic, "IEnumerable");
    generic.generic_arguments = Some(vec![inner]);
    let op = Operation {
        name: "test-op".into(),
        description: None,
        parameters: vec![make_param("messages", generic, true)],
        return_type: type_ref(TypeKind::Primitive, "void"),
        is_streaming: false,
        source_method_name: None,
    };
    let metadata = SdkMetadata {
        name: "TestSdk".into(),
        version: "1.0.0".into(),
        resources: vec![Resource {
            name: "thing".into(),
            operations: vec![op],
            ..make_resource("thing")
        }],
        auth_patterns: vec![],
        static_auth: None,
    };
    let (model, diags) = model_mapper::build(&metadata, &opts("test-cli"), &profile);
    assert!(model.resources[0].operations[0].can_wire_sdk_call);
    assert!(!diags.iter().any(|d| d.code == "CB307"));
}

// ================================================================
// Phase 2: Integration test — TestSdk fixture → GeneratorModel
// ================================================================

#[test]
fn testsdk_fixture_to_generator_model() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    let profile = TestProfile;
    let (model, diags) = model_mapper::build(
        &envelope.metadata,
        &opts("testsdk-cli"),
        &profile,
    );

    assert_eq!(model.cli_name, "testsdk-cli");
    assert!(!model.resources.is_empty());
    assert!(
        model.resources.len() >= 7,
        "Expected >= 7 resources, got {}",
        model.resources.len()
    );

    // Customer resource
    let customer = model.resources.iter().find(|r| r.name == "customer").unwrap();
    assert_eq!(customer.class_name, "Customer");
    assert!(!customer.operations.is_empty());
    assert!(customer.operations.iter().any(|o| o.name == "get"));

    // Auth present
    assert!(model.auth.is_some());

    // No error diagnostics
    let errors: Vec<_> = diags
        .iter()
        .filter(|d| d.severity == DiagnosticSeverity::Error)
        .collect();
    assert!(errors.is_empty(), "Unexpected errors: {:?}", errors);
}
