//! Tests for C# generator — CSharpProfile, model wrapper, post-processing.

use std::path::PathBuf;

use cli_builder_core::generator_model::*;
use cli_builder_core::model_mapper::{self, MapperOptions};
use cli_builder_core::models::*;

use crate::csharp_keywords;
use crate::csharp_mapper::CSharpProfile;
use crate::csharp_model::*;

fn tr(kind: TypeKind, name: &str) -> TypeRef {
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

fn testsdk_fixture_path() -> PathBuf {
    let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    PathBuf::from(manifest_dir)
        .join("../../..")
        .join("tests/fixtures/testsdk-metadata.json")
}

// ================================================================
// CSharpProfile: map_primitive_type
// ================================================================

#[test]
fn csharp_map_primitive_types() {
    let p = CSharpProfile;
    assert_eq!(p.map_primitive_type("string"), "string");
    assert_eq!(p.map_primitive_type("String"), "string");
    assert_eq!(p.map_primitive_type("int"), "int");
    assert_eq!(p.map_primitive_type("Int32"), "int");
    assert_eq!(p.map_primitive_type("long"), "long");
    assert_eq!(p.map_primitive_type("Int64"), "long");
    assert_eq!(p.map_primitive_type("bool"), "bool");
    assert_eq!(p.map_primitive_type("Boolean"), "bool");
    assert_eq!(p.map_primitive_type("float"), "float");
    assert_eq!(p.map_primitive_type("Single"), "float");
    assert_eq!(p.map_primitive_type("double"), "double");
    assert_eq!(p.map_primitive_type("Double"), "double");
    assert_eq!(p.map_primitive_type("decimal"), "decimal");
    assert_eq!(p.map_primitive_type("Decimal"), "decimal");
    assert_eq!(p.map_primitive_type("byte"), "byte");
    assert_eq!(p.map_primitive_type("short"), "short");
    assert_eq!(p.map_primitive_type("TimeSpan"), "string");
    assert_eq!(p.map_primitive_type("DateTime"), "string");
    assert_eq!(p.map_primitive_type("Guid"), "string");
    assert_eq!(p.map_primitive_type("void"), "void");
    assert_eq!(p.map_primitive_type("UnknownType"), "string");
}

// ================================================================
// CSharpProfile: map_cli_type nullable
// ================================================================

#[test]
fn csharp_nullable_int_appends_question_mark() {
    let p = CSharpProfile;
    let mut t = tr(TypeKind::Primitive, "int");
    t.is_nullable = true;
    assert_eq!(p.map_cli_type(&t, false), "int?");
}

#[test]
fn csharp_nullable_bool_appends_question_mark() {
    let p = CSharpProfile;
    let mut t = tr(TypeKind::Primitive, "bool");
    t.is_nullable = true;
    assert_eq!(p.map_cli_type(&t, false), "bool?");
}

#[test]
fn csharp_nullable_string_no_question_mark() {
    let p = CSharpProfile;
    let mut t = tr(TypeKind::Primitive, "string");
    t.is_nullable = true;
    assert_eq!(p.map_cli_type(&t, false), "string");
}

#[test]
fn csharp_nullable_enum_returns_string() {
    let p = CSharpProfile;
    let mut t = tr(TypeKind::Enum, "Status");
    t.is_nullable = true;
    assert_eq!(p.map_cli_type(&t, false), "string");
}

// ================================================================
// CSharpProfile: map_cli_type forCliParam
// ================================================================

#[test]
fn csharp_class_for_cli_param_returns_string() {
    let p = CSharpProfile;
    assert_eq!(p.map_cli_type(&tr(TypeKind::Class, "Options"), true), "string");
}

#[test]
fn csharp_class_for_return_preserves_name() {
    let p = CSharpProfile;
    assert_eq!(p.map_cli_type(&tr(TypeKind::Class, "Customer"), false), "Customer");
}

#[test]
fn csharp_generic_for_return_preserves_signature() {
    let p = CSharpProfile;
    let mut t = tr(TypeKind::Generic, "IEnumerable");
    t.generic_arguments = Some(vec![tr(TypeKind::Class, "Item")]);
    assert_eq!(p.map_cli_type(&t, false), "IEnumerable<Item>");
}

// ================================================================
// CSharpProfile: build_deserialization_type_name
// ================================================================

#[test]
fn csharp_deser_array() {
    let p = CSharpProfile;
    let mut t = tr(TypeKind::Array, "string[]");
    t.element_type = Some(Box::new(tr(TypeKind::Primitive, "string")));
    assert_eq!(p.build_deserialization_type_name(&t), "string[]");
}

#[test]
fn csharp_deser_dictionary_with_args() {
    let p = CSharpProfile;
    let mut t = tr(TypeKind::Dictionary, "Dictionary");
    t.generic_arguments = Some(vec![
        tr(TypeKind::Primitive, "string"),
        tr(TypeKind::Primitive, "string"),
    ]);
    assert_eq!(p.build_deserialization_type_name(&t), "Dictionary<string, string>");
}

#[test]
fn csharp_deser_dictionary_no_args() {
    let p = CSharpProfile;
    assert_eq!(
        p.build_deserialization_type_name(&tr(TypeKind::Dictionary, "Dictionary")),
        "Dictionary<string, object>"
    );
}

#[test]
fn csharp_deser_generic_list() {
    let p = CSharpProfile;
    let mut t = tr(TypeKind::Generic, "IEnumerable");
    t.generic_arguments = Some(vec![tr(TypeKind::Primitive, "string")]);
    assert_eq!(p.build_deserialization_type_name(&t), "List<string>");
}

// ================================================================
// Keywords and boilerplate
// ================================================================

#[test]
fn csharp_keywords_detected() {
    assert!(csharp_keywords::is_keyword("class"));
    assert!(csharp_keywords::is_keyword("int"));
    assert!(csharp_keywords::is_keyword("string"));
    assert!(csharp_keywords::is_keyword("return"));
    // Contextual
    assert!(csharp_keywords::is_keyword("var"));
    assert!(csharp_keywords::is_keyword("async"));
    assert!(csharp_keywords::is_keyword("record"));
    // Not keywords
    assert!(!csharp_keywords::is_keyword("customer"));
    assert!(!csharp_keywords::is_keyword("foobar"));
}

#[test]
fn csharp_boilerplate_detected() {
    assert!(csharp_keywords::is_boilerplate_name("JsonFormatter"));
    assert!(csharp_keywords::is_boilerplate_name("Program"));
    assert!(csharp_keywords::is_boilerplate_name("apiKey"));
    assert!(!csharp_keywords::is_boilerplate_name("customer"));
}

// ================================================================
// compute_conversion
// ================================================================

#[test]
fn conversion_real_enum_non_nullable() {
    let result = compute_conversion(Some(&TypeKind::Enum), Some("Status"), false, true);
    assert_eq!(result.as_deref(), Some("Enum.Parse<Status>({0})"));
}

#[test]
fn conversion_real_enum_nullable() {
    let result = compute_conversion(Some(&TypeKind::Enum), Some("Status"), true, true);
    let expr = result.unwrap();
    assert!(expr.contains("Enum.Parse<Status>"));
    assert!(expr.contains("is not null"));
    assert!(expr.contains("(Status?)null"));
}

#[test]
fn conversion_extensible_enum_returns_none() {
    // Extensible enums are handled by JSON deserializer, no conversion
    let result = compute_conversion(Some(&TypeKind::Enum), Some("Voice"), false, false);
    assert!(result.is_none());
}

#[test]
fn conversion_timespan_non_nullable() {
    let result = compute_conversion(Some(&TypeKind::Primitive), Some("TimeSpan"), false, false);
    assert_eq!(result.as_deref(), Some("TimeSpan.Parse({0})"));
}

#[test]
fn conversion_datetime_nullable() {
    let result = compute_conversion(Some(&TypeKind::Primitive), Some("DateTime"), true, false);
    let expr = result.unwrap();
    assert!(expr.contains("DateTime.Parse"));
    assert!(expr.contains("(DateTime?)null"));
}

#[test]
fn conversion_guid() {
    let result = compute_conversion(Some(&TypeKind::Primitive), Some("Guid"), false, false);
    assert_eq!(result.as_deref(), Some("Guid.Parse({0})"));
}

#[test]
fn conversion_plain_string_returns_none() {
    let result = compute_conversion(Some(&TypeKind::Primitive), Some("string"), false, false);
    assert!(result.is_none());
}

// ================================================================
// sanitize_default_value
// ================================================================

#[test]
fn default_null_returns_none() {
    let mut diags = vec![];
    assert!(sanitize_default_value(&serde_json::Value::Null, None, &mut diags).is_none());
}

#[test]
fn default_true() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!(true), None, &mut diags);
    assert_eq!(r.as_deref(), Some("true"));
}

#[test]
fn default_false() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!(false), None, &mut diags);
    assert_eq!(r.as_deref(), Some("false"));
}

#[test]
fn default_int() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!(42), Some("int"), &mut diags);
    assert_eq!(r.as_deref(), Some("42"));
}

#[test]
fn default_decimal_suffix() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!(9.99), Some("decimal"), &mut diags);
    assert_eq!(r.as_deref(), Some("9.99m"));
}

#[test]
fn default_double_suffix() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!(3.14), Some("double"), &mut diags);
    assert_eq!(r.as_deref(), Some("3.14d"));
}

#[test]
fn default_float_suffix() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!(1.5), Some("float"), &mut diags);
    assert_eq!(r.as_deref(), Some("1.5f"));
}

#[test]
fn default_string_verbatim() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!("hello"), None, &mut diags);
    assert_eq!(r.as_deref(), Some("@\"hello\""));
}

#[test]
fn default_string_escapes_quotes() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!("say \"hi\""), None, &mut diags);
    assert_eq!(r.as_deref(), Some("@\"say \"\"hi\"\"\""));
}

#[test]
fn default_array_rejected_with_cb302() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!([1, 2, 3]), None, &mut diags);
    assert!(r.is_none());
    assert!(diags.iter().any(|d| d.code == "CB302"));
}

#[test]
fn default_object_rejected_with_cb302() {
    let mut diags = vec![];
    let r = sanitize_default_value(&serde_json::json!({"key": "val"}), None, &mut diags);
    assert!(r.is_none());
    assert!(diags.iter().any(|d| d.code == "CB302"));
}

// ================================================================
// make_value_types_nullable
// ================================================================

#[test]
fn nullable_bool_becomes_bool_question() {
    let mut params = vec![CSharpFlatParameter {
        cli_flag: "active".into(),
        property_name: "Active".into(),
        csharp_type: "bool".into(),
        is_required: false,
        default_value_literal: None,
        description: None,
        enum_values: None,
        sdk_type_name: None,
        sdk_type_kind: None,
        sdk_type_is_nullable: false,
        conversion_expression: None,
        source_options_class_name: Some("Opts".into()),
    }];
    make_value_types_nullable(&mut params);
    assert_eq!(params[0].csharp_type, "bool?");
    assert_eq!(params[0].conversion_expression.as_deref(), Some("{0}.Value"));
}

#[test]
fn nullable_string_stays_string() {
    let mut params = vec![CSharpFlatParameter {
        cli_flag: "name".into(),
        property_name: "Name".into(),
        csharp_type: "string".into(),
        is_required: false,
        default_value_literal: None,
        description: None,
        enum_values: None,
        sdk_type_name: None,
        sdk_type_kind: None,
        sdk_type_is_nullable: false,
        conversion_expression: None,
        source_options_class_name: Some("Opts".into()),
    }];
    make_value_types_nullable(&mut params);
    assert_eq!(params[0].csharp_type, "string"); // No change
}

#[test]
fn nullable_direct_param_stays_unchanged() {
    let mut params = vec![CSharpFlatParameter {
        cli_flag: "count".into(),
        property_name: "count".into(),
        csharp_type: "int".into(),
        is_required: true,
        default_value_literal: None,
        description: None,
        enum_values: None,
        sdk_type_name: None,
        sdk_type_kind: None,
        sdk_type_is_nullable: false,
        conversion_expression: None,
        source_options_class_name: None, // No options class = direct param
    }];
    make_value_types_nullable(&mut params);
    assert_eq!(params[0].csharp_type, "int"); // No change — direct param
}

// ================================================================
// build_constructor_expression
// ================================================================

#[test]
fn ctor_string_auth() {
    let resource = ResourceModel {
        name: "customer".into(),
        class_name: "Customer".into(),
        description: None,
        operations: vec![],
        source_class_name: Some("CustomerService".into()),
        source_module: None,
        can_construct: true,
        constructor_auth: Some(ConstructorAuth {
            param_name: "apiKey".into(),
            type_name: "string".into(),
            type_module: None,
            is_plain_string: true,
        }),
        constructor_config_params: vec![],
        required_modules: vec![],
    };
    assert_eq!(build_constructor_expression(&resource).as_deref(), Some("credential"));
}

#[test]
fn ctor_typed_auth() {
    let resource = ResourceModel {
        name: "product".into(),
        class_name: "Product".into(),
        description: None,
        operations: vec![],
        source_class_name: Some("ProductApi".into()),
        source_module: None,
        can_construct: true,
        constructor_auth: Some(ConstructorAuth {
            param_name: "credential".into(),
            type_name: "TokenCredential".into(),
            type_module: Some("Sdk.Auth".into()),
            is_plain_string: false,
        }),
        constructor_config_params: vec![],
        required_modules: vec![],
    };
    assert_eq!(
        build_constructor_expression(&resource).as_deref(),
        Some("new TokenCredential(credential)")
    );
}

#[test]
fn ctor_multi_arg() {
    let resource = ResourceModel {
        name: "search".into(),
        class_name: "Search".into(),
        description: None,
        operations: vec![],
        source_class_name: Some("SearchClient".into()),
        source_module: None,
        can_construct: true,
        constructor_auth: Some(ConstructorAuth {
            param_name: "credential".into(),
            type_name: "ApiKeyCredential".into(),
            type_module: None,
            is_plain_string: false,
        }),
        constructor_config_params: vec![ConstructorConfigParam {
            cli_flag: "index".into(),
            var_name: "indexValue".into(),
            cli_type: "string".into(),
            is_required: true,
        }],
        required_modules: vec![],
    };
    assert_eq!(
        build_constructor_expression(&resource).as_deref(),
        Some("indexValue, new ApiKeyCredential(credential)")
    );
}

#[test]
fn ctor_no_auth_returns_none() {
    let resource = ResourceModel {
        name: "thing".into(),
        class_name: "Thing".into(),
        description: None,
        operations: vec![],
        source_class_name: None,
        source_module: None,
        can_construct: false,
        constructor_auth: None,
        constructor_config_params: vec![],
        required_modules: vec![],
    };
    assert!(build_constructor_expression(&resource).is_none());
}

// ================================================================
// sanitize_xml_value
// ================================================================

#[test]
fn xml_escapes_all_entities() {
    assert_eq!(sanitize_xml_value("a&b<c>d\"e'f"), "a&amp;b&lt;c&gt;d&quot;e&apos;f");
}

// ================================================================
// Integration: TestSdk fixture → CSharpGeneratorModel
// ================================================================

#[test]
fn testsdk_to_csharp_model() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    let profile = CSharpProfile;
    let (model, _) = model_mapper::build(
        &envelope.metadata,
        &MapperOptions { cli_name: Some("testsdk-cli".into()) },
        &profile,
    );

    let mut diags = vec![];
    let csharp = build_csharp_model(&model, &mut diags);

    assert_eq!(csharp.cli_name, "testsdk-cli");
    assert!(!csharp.resources.is_empty());
    assert!(csharp.resources.len() >= 7);

    // Customer resource
    let customer = csharp.resources.iter().find(|r| r.name == "customer").unwrap();
    assert_eq!(customer.class_name, "Customer");
    assert!(customer.constructor_expression.is_some());
    assert!(!customer.operations.is_empty());

    // Auth present
    assert!(csharp.auth.is_some());

    // No errors
    let errors: Vec<_> = diags.iter().filter(|d| d.severity == DiagnosticSeverity::Error).collect();
    assert!(errors.is_empty(), "Unexpected errors: {:?}", errors);
}
